//! CHAINLEASH GovernedVault.
//!
//! Holds the treasury (native CSPR) and enforces the agent's authority ON-CHAIN:
//! a per-action value cap + a counterparty allowlist on autonomous settlements,
//! a propose→human-approve path for over-cap (material) moves, a slashable bond,
//! and a per-decision CES event trail. The cap is enforced by the deployed
//! contract — not by any server — so a compromised agent backend physically
//! cannot exceed it. The `agent` and `owner` are distinct accounts so the
//! contract can require the human (`owner`) for material approvals and authority
//! increases, while the agent can only act routinely and tighten (never raise)
//! its own cap.

use odra::casper_types::U512;
use odra::prelude::*;

#[odra::odra_error]
pub enum Error {
    NotInitialized = 1,
    NotAgent = 2,
    NotOwner = 3,
    OverCap = 4,
    CounterpartyNotAllowed = 5,
    InsufficientTreasury = 6,
    CapNotLower = 7,
    NoSuchProposal = 8,
    ProposalAlreadyResolved = 9,
}

#[odra::odra_type]
pub struct Proposal {
    pub counterparty: Address,
    pub amount: U512,
    pub resolved: bool,
}

#[odra::event]
pub struct TreasuryDeposited {
    pub from: Address,
    pub amount: U512,
    pub treasury: U512,
}

#[odra::event]
pub struct BondDeposited {
    pub from: Address,
    pub amount: U512,
    pub bond: U512,
}

#[odra::event]
pub struct Settled {
    pub counterparty: Address,
    pub amount: U512,
    pub treasury: U512,
}

#[odra::event]
pub struct MaterialProposed {
    pub id: u32,
    pub counterparty: Address,
    pub amount: U512,
}

#[odra::event]
pub struct MaterialExecuted {
    pub id: u32,
    pub counterparty: Address,
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
pub struct AllowlistSet {
    pub counterparty: Address,
    pub allowed: bool,
}

#[odra::event]
pub struct ViolationRecorded {
    pub reason: String,
    pub count: u32,
}

#[odra::module]
pub struct GovernedVault {
    agent: Var<Address>,
    owner: Var<Address>,
    value_cap: Var<U512>,
    treasury: Var<U512>,
    bond: Var<U512>,
    violations: Var<u32>,
    next_id: Var<u32>,
    allowlist: Mapping<Address, bool>,
    proposals: Mapping<u32, Proposal>,
}

#[odra::module]
impl GovernedVault {
    /// Initialize with the agent + owner accounts and the initial per-action cap.
    pub fn init(&mut self, agent: Address, owner: Address, value_cap: U512) {
        self.agent.set(agent);
        self.owner.set(owner);
        self.value_cap.set(value_cap);
    }

    /// Fund the treasury with attached CSPR.
    #[odra(payable)]
    pub fn deposit_treasury(&mut self) {
        let amount = self.env().attached_value();
        let treasury = self.treasury.get_or_default() + amount;
        self.treasury.set(treasury);
        self.env().emit_event(TreasuryDeposited {
            from: self.env().caller(),
            amount,
            treasury,
        });
    }

    /// Post the agent's slashable bond with attached CSPR.
    #[odra(payable)]
    pub fn deposit_bond(&mut self) {
        let amount = self.env().attached_value();
        let bond = self.bond.get_or_default() + amount;
        self.bond.set(bond);
        self.env().emit_event(BondDeposited {
            from: self.env().caller(),
            amount,
            bond,
        });
    }

    /// Routine autonomous settlement by the agent. Reverts if over the cap,
    /// to a non-allowlisted counterparty, or beyond the treasury.
    pub fn settle(&mut self, counterparty: Address, amount: U512) {
        self.assert_agent();
        if amount > self.value_cap.get_or_default() {
            self.env().revert(Error::OverCap);
        }
        self.do_transfer(counterparty, amount);
        self.env().emit_event(Settled {
            counterparty,
            amount,
            treasury: self.treasury.get_or_default(),
        });
    }

    /// Agent proposes an over-cap (material) move; it does not execute until the
    /// owner approves. Returns the proposal id.
    pub fn propose_material(&mut self, counterparty: Address, amount: U512) -> u32 {
        self.assert_agent();
        let id = self.next_id.get_or_default();
        self.next_id.set(id + 1);
        self.proposals.set(
            &id,
            Proposal {
                counterparty,
                amount,
                resolved: false,
            },
        );
        self.env().emit_event(MaterialProposed {
            id,
            counterparty,
            amount,
        });
        id
    }

    /// Owner (human) approves and executes a pending material move.
    pub fn approve_material(&mut self, id: u32) {
        self.assert_owner();
        let mut proposal = self
            .proposals
            .get(&id)
            .unwrap_or_revert_with(self, Error::NoSuchProposal);
        if proposal.resolved {
            self.env().revert(Error::ProposalAlreadyResolved);
        }
        self.do_transfer(proposal.counterparty, proposal.amount);
        proposal.resolved = true;
        self.env().emit_event(MaterialExecuted {
            id,
            counterparty: proposal.counterparty,
            amount: proposal.amount,
        });
        self.proposals.set(&id, proposal);
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

    /// Only the owner (human) may RAISE the cap.
    pub fn raise_cap(&mut self, new_cap: U512) {
        self.assert_owner();
        let old_cap = self.value_cap.get_or_default();
        self.value_cap.set(new_cap);
        self.env().emit_event(CapRaised { old_cap, new_cap });
    }

    /// Owner manages the counterparty allowlist.
    pub fn set_allowlist(&mut self, counterparty: Address, allowed: bool) {
        self.assert_owner();
        self.allowlist.set(&counterparty, allowed);
        self.env().emit_event(AllowlistSet {
            counterparty,
            allowed,
        });
    }

    /// Owner records an on-chain policy violation against the bond.
    pub fn record_violation(&mut self, reason: String) {
        self.assert_owner();
        let count = self.violations.get_or_default() + 1;
        self.violations.set(count);
        self.env().emit_event(ViolationRecorded { reason, count });
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
    pub fn treasury(&self) -> U512 {
        self.treasury.get_or_default()
    }
    pub fn bond(&self) -> U512 {
        self.bond.get_or_default()
    }
    pub fn violations(&self) -> u32 {
        self.violations.get_or_default()
    }
    pub fn is_allowed(&self, counterparty: Address) -> bool {
        self.allowlist.get_or_default(&counterparty)
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

    fn do_transfer(&mut self, counterparty: Address, amount: U512) {
        if !self.allowlist.get_or_default(&counterparty) {
            self.env().revert(Error::CounterpartyNotAllowed);
        }
        let treasury = self.treasury.get_or_default();
        if amount > treasury {
            self.env().revert(Error::InsufficientTreasury);
        }
        self.treasury.set(treasury - amount);
        self.env().transfer_tokens(&counterparty, &amount);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use odra::host::{Deployer, HostRef};

    fn u(n: u64) -> U512 {
        U512::from(n)
    }

    fn setup() -> (odra::host::HostEnv, GovernedVaultHostRef, Address, Address, Address) {
        let env = odra_test::env();
        let owner = env.get_account(0);
        let agent = env.get_account(1);
        let counterparty = env.get_account(2);
        let init = GovernedVaultInitArgs {
            agent,
            owner,
            value_cap: u(1000),
        };
        let mut vault = GovernedVault::deploy(&env, init);
        env.set_caller(owner);
        vault.set_allowlist(counterparty, true);
        env.set_caller(owner);
        vault.with_tokens(u(10_000)).deposit_treasury();
        (env, vault, agent, owner, counterparty)
    }

    #[test]
    fn init_state() {
        let (_env, vault, agent, owner, _cp) = setup();
        assert_eq!(vault.get_agent(), agent);
        assert_eq!(vault.get_owner(), owner);
        assert_eq!(vault.value_cap(), u(1000));
        assert_eq!(vault.treasury(), u(10_000));
    }

    #[test]
    fn settle_under_cap_succeeds() {
        let (env, mut vault, agent, _owner, cp) = setup();
        env.set_caller(agent);
        vault.settle(cp, u(800));
        assert_eq!(vault.treasury(), u(9_200));
    }

    #[test]
    fn settle_over_cap_reverts() {
        let (env, mut vault, agent, _owner, cp) = setup();
        env.set_caller(agent);
        assert_eq!(vault.try_settle(cp, u(1_001)), Err(Error::OverCap.into()));
    }

    #[test]
    fn settle_to_non_allowlisted_reverts() {
        let (env, mut vault, agent, _owner, _cp) = setup();
        let stranger = env.get_account(3);
        env.set_caller(agent);
        assert_eq!(
            vault.try_settle(stranger, u(100)),
            Err(Error::CounterpartyNotAllowed.into())
        );
    }

    #[test]
    fn settle_by_non_agent_reverts() {
        let (env, mut vault, _agent, owner, cp) = setup();
        env.set_caller(owner);
        assert_eq!(vault.try_settle(cp, u(100)), Err(Error::NotAgent.into()));
    }

    #[test]
    fn material_propose_then_owner_approves() {
        let (env, mut vault, agent, owner, cp) = setup();
        env.set_caller(agent);
        let id = vault.propose_material(cp, u(5_000));
        // agent cannot self-approve
        assert_eq!(vault.try_approve_material(id), Err(Error::NotOwner.into()));
        env.set_caller(owner);
        vault.approve_material(id);
        assert_eq!(vault.treasury(), u(5_000));
        // cannot approve twice
        assert_eq!(
            vault.try_approve_material(id),
            Err(Error::ProposalAlreadyResolved.into())
        );
    }

    #[test]
    fn tighten_only_down() {
        let (env, mut vault, agent, _owner, _cp) = setup();
        env.set_caller(agent);
        vault.tighten_cap(u(500));
        assert_eq!(vault.value_cap(), u(500));
        assert_eq!(vault.try_tighten_cap(u(600)), Err(Error::CapNotLower.into()));
    }

    #[test]
    fn only_owner_raises_cap() {
        let (env, mut vault, agent, owner, _cp) = setup();
        env.set_caller(agent);
        assert_eq!(vault.try_raise_cap(u(2_000)), Err(Error::NotOwner.into()));
        env.set_caller(owner);
        vault.raise_cap(u(2_000));
        assert_eq!(vault.value_cap(), u(2_000));
    }

    #[test]
    fn bond_and_violations() {
        let (env, mut vault, _agent, owner, _cp) = setup();
        env.set_caller(owner);
        vault.with_tokens(u(3_000)).deposit_bond();
        assert_eq!(vault.bond(), u(3_000));
        env.set_caller(owner);
        vault.record_violation(String::from("velocity anomaly"));
        assert_eq!(vault.violations(), 1);
    }
}
