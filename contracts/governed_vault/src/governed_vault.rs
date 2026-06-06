//! CHAINLEASH GovernedVault — the chain-enforced leash over CSPR staking.
//!
//! The vault HOLDS the treasury's CSPR and DELEGATES it to validators on the
//! agent's instruction — but the chain (this deployed contract) enforces the
//! limits, not any server:
//!   * per-action value cap on how much the agent can (un)delegate at once,
//!   * a validator ALLOWLIST (the agent can only stake to approved validators),
//!   * a propose -> human-approve flow for over-cap "material" moves,
//!   * a posted CSPR bond (held in the vault; policy violations are recorded
//!     on-chain as auditable events — Casper has no native slashing),
//!   * and crucially: the AGENT HAS NO WITHDRAW PATH. It can rebalance stake
//!     among approved validators within the cap, but can NEVER move CSPR out of
//!     the vault. Only the owner (human/exchange) can withdraw. So a fully
//!     compromised agent can mis-delegate within the leash — it cannot steal.
//!
//! Combined with the treasury account's native weighted keys (agent key below
//! the key_management threshold), the agent also cannot raise its own authority
//! or rotate keys. Built for real Casper staking — Casper's actual on-chain
//! economic primitive — not a fictional DeFi venue.

use odra::casper_types::{PublicKey, U512};
use odra::prelude::*;

#[odra::odra_error]
pub enum Error {
    NotInitialized = 1,
    NotAgent = 2,
    NotOwner = 3,
    OverCap = 4,
    ValidatorNotAllowed = 5,
    NoSuchProposal = 6,
    ProposalAlreadyResolved = 7,
    AlreadyInitialized = 8,
    CapNotLower = 9,
    InsufficientFreeBalance = 10,
    Paused = 11,
    PerValidatorCapExceeded = 12,
    RateLimited = 13,
}

/// A pending over-cap (material) staking move awaiting owner approval.
#[odra::odra_type]
pub struct Proposal {
    pub validator: PublicKey,
    pub amount: U512,
    pub undelegate: bool, // false = delegate, true = undelegate
    pub resolved: bool,
}

#[odra::event]
pub struct Funded {
    pub from: Address,
    pub amount: U512,
}
#[odra::event]
pub struct BondDeposited {
    pub from: Address,
    pub amount: U512,
    pub bond: U512,
}
#[odra::event]
pub struct Delegated {
    pub validator: PublicKey,
    pub amount: U512,
}
#[odra::event]
pub struct Undelegated {
    pub validator: PublicKey,
    pub amount: U512,
}
#[odra::event]
pub struct Redelegated {
    pub from: PublicKey,
    pub to: PublicKey,
    pub amount: U512,
}
#[odra::event]
pub struct MaterialProposed {
    pub id: u32,
    pub validator: PublicKey,
    pub amount: U512,
    pub undelegate: bool,
}
#[odra::event]
pub struct MaterialExecuted {
    pub id: u32,
    pub validator: PublicKey,
    pub amount: U512,
}
#[odra::event]
pub struct CapTightened {
    pub old_cap: U512,
    pub new_cap: U512,
}
#[odra::event]
pub struct CapRaised {
    pub old_cap: U512,
    pub new_cap: U512,
}
#[odra::event]
pub struct ValidatorAllowed {
    pub validator: PublicKey,
    pub allowed: bool,
}
#[odra::event]
pub struct ViolationRecorded {
    pub reason: String,
    pub count: u32,
}
#[odra::event]
pub struct Withdrawn {
    pub to: Address,
    pub amount: U512,
}
#[odra::event]
pub struct PausedSet {
    pub paused: bool,
}
#[odra::event]
pub struct PerValidatorCapSet {
    pub max: U512,
}
#[odra::event]
pub struct ActionIntervalSet {
    pub interval_ms: u64,
}

#[odra::module]
pub struct GovernedVault {
    agent: Var<Address>,
    owner: Var<Address>,
    value_cap: Var<U512>, // max CSPR the agent may (un)delegate per action
    bond: Var<U512>,
    violations: Var<u32>,
    next_id: Var<u32>,
    allowlist: Mapping<PublicKey, bool>,
    proposals: Mapping<u32, Proposal>,
    // --- richer policy (owner-controlled) ---
    paused: Var<bool>,                // kill-switch: when true, all agent moves revert
    max_per_validator: Var<U512>,     // per-validator stake ceiling (0 = unlimited) — decentralization
    min_action_interval: Var<u64>,    // ms cooldown between agent moves (0 = disabled) — anti-thrash
    last_action_time: Var<u64>,       // block time (ms) of the agent's last move
}

#[odra::module]
impl GovernedVault {
    /// One-time init (called once after deploy via a normal call; not an Odra
    /// constructor, so install needs no constructor args).
    pub fn initialize(&mut self, agent: Address, owner: Address, value_cap: U512) {
        if self.agent.get().is_some() {
            self.env().revert(Error::AlreadyInitialized);
        }
        self.agent.set(agent);
        self.owner.set(owner);
        self.value_cap.set(value_cap);
    }

    /// Fund the vault with CSPR to be staked (the exchange/treasury deposits here).
    #[odra(payable)]
    pub fn deposit_treasury(&mut self) {
        let amount = self.env().attached_value();
        self.env().emit_event(Funded { from: self.env().caller(), amount });
    }

    /// Post the agent's CSPR bond (held in the vault; violations are recorded on-chain).
    #[odra(payable)]
    pub fn deposit_bond(&mut self) {
        let amount = self.env().attached_value();
        let bond = self.bond.get_or_default() + amount;
        self.bond.set(bond);
        self.env().emit_event(BondDeposited { from: self.env().caller(), amount, bond });
    }

    /// Routine autonomous delegation by the agent — capped + allowlisted.
    pub fn delegate(&mut self, validator: PublicKey, amount: U512) {
        self.assert_agent();
        self.assert_not_paused();
        self.assert_within_cap(amount);
        self.assert_validator_allowed(&validator);
        self.assert_per_validator_cap(&validator, amount);
        self.assert_rate_ok();
        self.env().delegate(validator.clone(), amount);
        self.env().emit_event(Delegated { validator, amount });
    }

    /// Routine autonomous undelegation by the agent — capped. Funds return to
    /// the VAULT (after unbonding), never to the agent; the agent cannot drain.
    pub fn undelegate(&mut self, validator: PublicKey, amount: U512) {
        self.assert_agent();
        self.assert_not_paused();
        self.assert_within_cap(amount);
        self.assert_rate_ok();
        self.env().undelegate(validator.clone(), amount);
        self.env().emit_event(Undelegated { validator, amount });
    }

    /// Routine autonomous REDELEGATION — move stake from one validator to another in a
    /// single native transaction: the funds unbond from the old validator and auto-move
    /// to the new one (Casper's standard ~7-era unbonding applies — redelegate is not
    /// instant), so the agent never has to hold the CSPR or come back to re-stake, and
    /// the destination is committed on-chain. Capped, and the DESTINATION validator must
    /// be allowlisted (you may leave any validator, but only move INTO an approved one).
    /// Odra 2.7 doesn't wrap the auction's redelegate, so we call it directly from the
    /// vault's own purse.
    pub fn redelegate(&mut self, validator: PublicKey, new_validator: PublicKey, amount: U512) {
        self.assert_agent();
        self.assert_not_paused();
        self.assert_within_cap(amount);
        self.assert_validator_allowed(&new_validator);
        self.assert_per_validator_cap(&new_validator, amount);
        self.assert_rate_ok();
        self.do_redelegate(validator.clone(), new_validator.clone(), amount);
        self.env().emit_event(Redelegated { from: validator, to: new_validator, amount });
    }

    /// Agent proposes an over-cap (material) (un)delegation; executes only after
    /// the owner approves. Returns the proposal id.
    pub fn propose_material(&mut self, validator: PublicKey, amount: U512, undelegate: bool) -> u32 {
        self.assert_agent();
        self.assert_not_paused();
        let id = self.next_id.get_or_default();
        self.next_id.set(id + 1);
        self.proposals.set(&id, Proposal { validator: validator.clone(), amount, undelegate, resolved: false });
        self.env().emit_event(MaterialProposed { id, validator, amount, undelegate });
        id
    }

    /// Owner (human/exchange) approves and executes a pending material move.
    pub fn approve_material(&mut self, id: u32) {
        self.assert_owner();
        let mut p = self.proposals.get(&id).unwrap_or_revert_with(self, Error::NoSuchProposal);
        if p.resolved {
            self.env().revert(Error::ProposalAlreadyResolved);
        }
        if p.undelegate {
            self.env().undelegate(p.validator.clone(), p.amount);
        } else {
            self.assert_validator_allowed(&p.validator);
            self.env().delegate(p.validator.clone(), p.amount);
        }
        p.resolved = true;
        self.env().emit_event(MaterialExecuted { id, validator: p.validator.clone(), amount: p.amount });
        self.proposals.set(&id, p);
    }

    /// Agent/overseer may only LOWER the cap (tighten the leash).
    pub fn tighten_cap(&mut self, new_cap: U512) {
        self.assert_agent();
        let old_cap = self.value_cap.get_or_default();
        if new_cap >= old_cap {
            self.env().revert(Error::CapNotLower);
        }
        self.value_cap.set(new_cap);
        self.env().emit_event(CapTightened { old_cap, new_cap });
    }

    /// Only the owner may RAISE the cap.
    pub fn raise_cap(&mut self, new_cap: U512) {
        self.assert_owner();
        let old_cap = self.value_cap.get_or_default();
        self.value_cap.set(new_cap);
        self.env().emit_event(CapRaised { old_cap, new_cap });
    }

    /// Owner manages the validator allowlist.
    pub fn set_validator(&mut self, validator: PublicKey, allowed: bool) {
        self.assert_owner();
        self.allowlist.set(&validator, allowed);
        self.env().emit_event(ValidatorAllowed { validator, allowed });
    }

    /// Owner kill-switch — while paused, every agent move (delegate/undelegate/
    /// redelegate/propose) reverts. Owner operations are unaffected.
    pub fn set_paused(&mut self, paused: bool) {
        self.assert_owner();
        self.paused.set(paused);
        self.env().emit_event(PausedSet { paused });
    }

    /// Owner sets the per-validator stake ceiling (0 = unlimited) — caps how much the
    /// agent may concentrate on any single validator, enforcing decentralization.
    pub fn set_max_per_validator(&mut self, max: U512) {
        self.assert_owner();
        self.max_per_validator.set(max);
        self.env().emit_event(PerValidatorCapSet { max });
    }

    /// Owner sets the minimum milliseconds between agent moves (0 = disabled) — an
    /// anti-thrash rate limit so a buggy or hijacked agent can't churn the stake.
    pub fn set_action_interval(&mut self, interval_ms: u64) {
        self.assert_owner();
        self.min_action_interval.set(interval_ms);
        self.env().emit_event(ActionIntervalSet { interval_ms });
    }

    /// Owner records an on-chain policy violation against the bond.
    pub fn record_violation(&mut self, reason: String) {
        self.assert_owner();
        let count = self.violations.get_or_default() + 1;
        self.violations.set(count);
        self.env().emit_event(ViolationRecorded { reason, count });
    }

    /// Owner-ONLY withdrawal of free (un-delegated) CSPR from the vault. The
    /// agent has no equivalent — this is the "agent cannot steal" guarantee.
    pub fn withdraw(&mut self, amount: U512) {
        self.assert_owner();
        let owner = self.owner.get().unwrap_or_revert_with(self, Error::NotInitialized);
        self.env().transfer_tokens(&owner, &amount);
        self.env().emit_event(Withdrawn { to: owner, amount });
    }

    // ---- views ----
    pub fn get_agent(&self) -> Address {
        self.agent.get().unwrap_or_revert_with(self, Error::NotInitialized)
    }
    pub fn get_owner(&self) -> Address {
        self.owner.get().unwrap_or_revert_with(self, Error::NotInitialized)
    }
    pub fn value_cap(&self) -> U512 {
        self.value_cap.get_or_default()
    }
    pub fn bond(&self) -> U512 {
        self.bond.get_or_default()
    }
    pub fn violations(&self) -> u32 {
        self.violations.get_or_default()
    }
    pub fn is_validator_allowed(&self, validator: PublicKey) -> bool {
        self.allowlist.get_or_default(&validator)
    }
    pub fn delegated_to(&self, validator: PublicKey) -> U512 {
        self.env().delegated_amount(validator)
    }
    pub fn is_paused(&self) -> bool {
        self.paused.get_or_default()
    }
    pub fn max_per_validator(&self) -> U512 {
        self.max_per_validator.get_or_default()
    }
    pub fn action_interval(&self) -> u64 {
        self.min_action_interval.get_or_default()
    }

    // ---- internal ----
    fn assert_agent(&self) {
        let agent = self.agent.get().unwrap_or_revert_with(self, Error::NotInitialized);
        if self.env().caller() != agent {
            self.env().revert(Error::NotAgent);
        }
    }
    fn assert_owner(&self) {
        let owner = self.owner.get().unwrap_or_revert_with(self, Error::NotInitialized);
        if self.env().caller() != owner {
            self.env().revert(Error::NotOwner);
        }
    }
    fn assert_within_cap(&self, amount: U512) {
        if amount > self.value_cap.get_or_default() {
            self.env().revert(Error::OverCap);
        }
    }
    fn assert_validator_allowed(&self, validator: &PublicKey) {
        if !self.allowlist.get_or_default(validator) {
            self.env().revert(Error::ValidatorNotAllowed);
        }
    }
    fn assert_not_paused(&self) {
        if self.paused.get_or_default() {
            self.env().revert(Error::Paused);
        }
    }
    /// Reject a delegation that would push the validator's stake over the per-validator
    /// ceiling (0 = unlimited). Reads the contract's current delegation from the auction.
    fn assert_per_validator_cap(&self, validator: &PublicKey, add: U512) {
        let max = self.max_per_validator.get_or_default();
        if max > U512::zero() && self.env().delegated_amount(validator.clone()) + add > max {
            self.env().revert(Error::PerValidatorCapExceeded);
        }
    }
    /// Enforce the cooldown between agent moves (0 = disabled) and record the time of
    /// this move. The first move is always allowed (no prior timestamp).
    fn assert_rate_ok(&mut self) {
        let interval = self.min_action_interval.get_or_default();
        if interval == 0 {
            return;
        }
        let now = self.env().get_block_time();
        let last = self.last_action_time.get_or_default();
        if last != 0 && now < last + interval {
            self.env().revert(Error::RateLimited);
        }
        self.last_action_time.set(now);
    }

    /// Invoke the Casper auction's native `redelegate` from the vault's main purse.
    /// Odra 2.7 wraps delegate/undelegate but not redelegate, so we call the system
    /// auction directly — mirroring Odra's own delegate host function.
    #[cfg(target_arch = "wasm32")]
    fn do_redelegate(&self, validator: PublicKey, new_validator: PublicKey, amount: U512) {
        use casper_contract::contract_api::{runtime, system};
        use odra::casper_types::{system::auction, RuntimeArgs, URef};
        let purse: URef = runtime::get_key("__contract_main_purse")
            .and_then(|k| k.as_uref().copied())
            .unwrap_or_revert_with(self, Error::NotInitialized);
        let auction_hash = system::get_auction();
        let mut args = RuntimeArgs::new();
        args.insert(auction::ARG_DELEGATOR_PURSE, purse).unwrap();
        args.insert(auction::ARG_VALIDATOR, validator).unwrap();
        args.insert(auction::ARG_NEW_VALIDATOR, new_validator).unwrap();
        args.insert(auction::ARG_AMOUNT, amount).unwrap();
        runtime::call_contract::<U512>(auction_hash, auction::METHOD_REDELEGATE, args);
    }

    /// The OdraVM test backend has no auction; the real redelegate is verified on testnet.
    #[cfg(not(target_arch = "wasm32"))]
    fn do_redelegate(&self, _validator: PublicKey, _new_validator: PublicKey, _amount: U512) {}
}

#[cfg(test)]
mod tests {
    use super::*;
    use odra::host::{Deployer, HostRef, NoArgs};

    fn u(n: u64) -> U512 {
        U512::from(n) * U512::from(1_000_000_000u64) // n CSPR, in motes
    }

    // env.get_validator(n) returns a validator pre-registered in the test VM.
    fn setup() -> (odra::host::HostEnv, GovernedVaultHostRef, Address, Address, PublicKey, PublicKey) {
        let env = odra_test::env();
        let owner = env.get_account(0);
        let agent = env.get_account(1);
        let v1 = env.get_validator(0);
        let v2 = env.get_validator(1);
        let mut vault = GovernedVault::deploy(&env, NoArgs);
        vault.initialize(agent, owner, u(1000));
        env.set_caller(owner);
        vault.set_validator(v1.clone(), true);
        env.set_caller(owner);
        vault.with_tokens(u(100_000)).deposit_treasury();
        (env, vault, agent, owner, v1, v2)
    }

    #[test]
    fn init_state() {
        let (_e, vault, agent, owner, _v1, _v2) = setup();
        assert_eq!(vault.get_agent(), agent);
        assert_eq!(vault.get_owner(), owner);
        assert_eq!(vault.value_cap(), u(1000));
    }

    #[test]
    fn delegate_under_cap_to_allowed_validator() {
        let (env, mut vault, agent, _o, v1, _v2) = setup();
        env.set_caller(agent);
        vault.delegate(v1.clone(), u(800));
        assert_eq!(vault.delegated_to(v1), u(800));
    }

    #[test]
    fn delegate_over_cap_reverts() {
        let (env, mut vault, agent, _o, v1, _v2) = setup();
        env.set_caller(agent);
        assert_eq!(vault.try_delegate(v1, u(1001)), Err(Error::OverCap.into()));
    }

    #[test]
    fn delegate_to_unapproved_validator_reverts() {
        let (env, mut vault, agent, _o, _v1, v2) = setup();
        env.set_caller(agent);
        assert_eq!(vault.try_delegate(v2, u(500)), Err(Error::ValidatorNotAllowed.into()));
    }

    #[test]
    fn delegate_by_non_agent_reverts() {
        let (env, mut vault, _a, owner, v1, _v2) = setup();
        env.set_caller(owner);
        assert_eq!(vault.try_delegate(v1, u(500)), Err(Error::NotAgent.into()));
    }

    #[test]
    fn redelegate_under_cap_to_allowed_new_validator() {
        // The OdraVM backend has no auction, so do_redelegate is a no-op here; this
        // verifies the guard chain + event path. The real move is verified on testnet.
        let (env, mut vault, agent, owner, v1, v2) = setup();
        env.set_caller(owner);
        vault.set_validator(v2.clone(), true);
        env.set_caller(agent);
        vault.redelegate(v1, v2, u(500));
    }

    #[test]
    fn redelegate_over_cap_reverts() {
        let (env, mut vault, agent, _o, v1, v2) = setup();
        env.set_caller(agent);
        assert_eq!(vault.try_redelegate(v1, v2, u(1001)), Err(Error::OverCap.into()));
    }

    #[test]
    fn redelegate_to_unapproved_new_validator_reverts() {
        let (env, mut vault, agent, _o, v1, v2) = setup();
        env.set_caller(agent);
        // v2 is not allowlisted in setup → moving INTO it is rejected.
        assert_eq!(vault.try_redelegate(v1, v2, u(500)), Err(Error::ValidatorNotAllowed.into()));
    }

    #[test]
    fn redelegate_by_non_agent_reverts() {
        let (env, mut vault, _a, owner, v1, v2) = setup();
        env.set_caller(owner);
        assert_eq!(vault.try_redelegate(v1, v2, u(500)), Err(Error::NotAgent.into()));
    }

    #[test]
    fn kill_switch_blocks_agent_then_unpause_allows() {
        let (env, mut vault, agent, owner, v1, _v2) = setup();
        env.set_caller(owner);
        vault.set_paused(true);
        assert!(vault.is_paused());
        env.set_caller(agent);
        assert_eq!(vault.try_delegate(v1.clone(), u(500)), Err(Error::Paused.into()));
        env.set_caller(owner);
        vault.set_paused(false);
        env.set_caller(agent);
        vault.delegate(v1, u(500)); // allowed once unpaused
    }

    #[test]
    fn per_validator_cap_enforced() {
        let (env, mut vault, agent, owner, v1, _v2) = setup();
        env.set_caller(owner);
        vault.set_max_per_validator(u(600));
        env.set_caller(agent);
        vault.delegate(v1.clone(), u(500)); // 0 + 500 ≤ 600 ok
        assert_eq!(vault.try_delegate(v1, u(200)), Err(Error::PerValidatorCapExceeded.into())); // 500 + 200 > 600
    }

    #[test]
    fn action_cooldown_rate_limits() {
        let (env, mut vault, agent, owner, v1, _v2) = setup();
        env.set_caller(owner);
        vault.set_action_interval(5_000);
        env.advance_block_time(10_000);
        env.set_caller(agent);
        vault.delegate(v1.clone(), u(100)); // first move ok
        assert_eq!(vault.try_delegate(v1.clone(), u(100)), Err(Error::RateLimited.into())); // too soon
        env.advance_block_time(5_000);
        vault.delegate(v1, u(100)); // cooldown elapsed → ok
    }

    #[test]
    fn material_propose_then_owner_approves() {
        let (env, mut vault, agent, owner, _v1, v2) = setup();
        env.set_caller(owner);
        vault.set_validator(v2.clone(), true);
        env.set_caller(agent);
        let id = vault.propose_material(v2.clone(), u(5000), false); // over cap
        assert_eq!(vault.try_approve_material(id), Err(Error::NotOwner.into()));
        env.set_caller(owner);
        vault.approve_material(id);
        assert_eq!(vault.delegated_to(v2), u(5000));
        assert_eq!(vault.try_approve_material(id), Err(Error::ProposalAlreadyResolved.into()));
    }

    #[test]
    fn tighten_only_down() {
        let (env, mut vault, agent, _o, _v1, _v2) = setup();
        env.set_caller(agent);
        vault.tighten_cap(u(500));
        assert_eq!(vault.value_cap(), u(500));
        assert_eq!(vault.try_tighten_cap(u(600)), Err(Error::CapNotLower.into()));
    }

    #[test]
    fn only_owner_raises_cap() {
        let (env, mut vault, agent, owner, _v1, _v2) = setup();
        env.set_caller(agent);
        assert_eq!(vault.try_raise_cap(u(2000)), Err(Error::NotOwner.into()));
        env.set_caller(owner);
        vault.raise_cap(u(2000));
        assert_eq!(vault.value_cap(), u(2000));
    }

    #[test]
    fn agent_cannot_withdraw_only_owner_can() {
        let (env, mut vault, agent, owner, _v1, _v2) = setup();
        env.set_caller(agent);
        assert_eq!(vault.try_withdraw(u(100)), Err(Error::NotOwner.into()));
        env.set_caller(owner);
        vault.withdraw(u(100));
    }

    #[test]
    fn bond_and_violations() {
        let (env, mut vault, _a, owner, _v1, _v2) = setup();
        env.set_caller(owner);
        vault.with_tokens(u(3000)).deposit_bond();
        assert_eq!(vault.bond(), u(3000));
        env.set_caller(owner);
        vault.record_violation(String::from("commission hike"));
        assert_eq!(vault.violations(), 1);
    }
}
