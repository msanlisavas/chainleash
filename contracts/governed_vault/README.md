# GovernedVault

The on-chain **leash** for CHAINLEASH — an Odra (Casper 2.0) contract that **holds** a
treasury's CSPR and lets an autonomous agent stake it, but only within limits the chain
itself enforces. The agent can rebalance delegations; it can never withdraw, never raise
its own authority, and forfeits its bond on a violation.

The vault delegates from **its own purse** (the auction identifies the delegator by purse,
not public key), so the per-action cap and validator allowlist are **fully chain-enforced**
— not advisory, not server-side.

## Roles

- **installer** — recorded in the `init` constructor at install; the only address allowed to
  call `initialize`. Closes the front-run window on a fresh deploy.
- **agent** — the autonomous key. May `delegate` / `undelegate` / `redelegate` within the
  leash, and `propose_material`. Cannot withdraw or change policy.
- **owner** — the human / institution (any account, including an M-of-N multisig). Co-signs
  material moves (`approve_material`), sets policy, slashes/returns the bond, withdraws, and
  can recover/rotate the agent or transfer ownership.

## Entry points

**Agent (capped + allowlisted, chain-rejected otherwise):**
`delegate`, `undelegate`, `redelegate`, `propose_material`, `deposit_bond`, and
`tighten_cap` (the agent may only *lower* its own cap, never raise it).

**Owner:** `approve_material`, `raise_cap`, `set_validator`, `set_paused`
(kill-switch), `set_max_per_validator`, `set_action_interval`, `record_violation`,
`slash_bond`, `return_bond`, `withdraw`, `transfer_ownership`, `set_agent`.

**Installer:** `init` (constructor, auto), `initialize(agent, owner, value_cap)`.

**Payable:** `deposit_treasury`, `deposit_bond` (via Odra's `proxy_caller`).

**Views (gas-free):** `get_agent`, `get_owner`, `value_cap`, `bond`, `violations`,
`is_validator_allowed`, `delegated_to`, `committed_to`, `is_paused`, `max_per_validator`,
`action_interval`, `get_proposal`, `next_proposal_id`, `total_balance`, `free_balance`,
`get_installer`, `is_initialized`, `get_bond_holder`.

## The leash (enforced invariants)

- **Per-action cap** — any single move > `value_cap` reverts `OverCap` (must go through
  `propose_material` → owner `approve_material`).
- **Validator allowlist** — delegating to a non-allowlisted validator reverts `ValidatorNotAllowed`.
- **Per-validator cap** — keeps the agent from over-concentrating stake; reverts `PerValidatorCapExceeded`
  (tracked by an in-contract `committed` accumulator, so it can't be raced).
- **Action cooldown** — anti-thrash; reverts `RateLimited`.
- **Kill-switch** — when paused, every agent move reverts `Paused`.
- **No agent withdraw** — only the owner can `withdraw`, and `withdraw` reserves the bond
  (`InsufficientFreeBalance` otherwise).
- **Slashable bond** — `slash_bond` forfeits the agent's posted bond to the owner.

Error codes: `NotInitialized(1) NotAgent(2) NotOwner(3) OverCap(4) ValidatorNotAllowed(5)
NoSuchProposal(6) ProposalAlreadyResolved(7) AlreadyInitialized(8) CapNotLower(9)
InsufficientFreeBalance(10) Paused(11) PerValidatorCapExceeded(12) RateLimited(13)
NotInstaller(14)`.

## Build / test

`casper-types` doesn't host-compile on Windows, so build/test run in the `chainleash-odra`
Linux container (see the [RUNBOOK](../../RUNBOOK.md)):

```
cargo odra test          # 26/26
cargo odra build         # -> wasm/GovernedVault.wasm (~295 KB)
```

Deploy is via the C# SDK (`SessionBuilder.Wasm(...).InstallOrUpgrade()`); `initialize` is
called separately after install. See the RUNBOOK and `scripts/onboard.ps1`.
