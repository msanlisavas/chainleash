# Changelog

Changelog for `governed_vault` — the on-chain leash for CHAINLEASH.

## [0.6.0] - 2026-06-10
### Security
- `deposit_bond` is no longer permissionless: the bond may only be OPENED by a principal
  (agent or owner), and while a bond is outstanding only the recorded holder may top it
  up — closes a fund-misdirection hole where anyone could attach 1 mote to overwrite
  `bond_holder` and capture the pooled bond on `return_bond` (`UnauthorizedBondDeposit=17`).
- `propose_material` now respects the action cooldown, so a hijacked agent can no longer
  spam permanent proposal storage faster than the owner-set interval (0 = disabled;
  the onboard scripts arm 30s by default).
- `initialize` / `set_agent` / `transfer_ownership` reject an agent==owner collision,
  which would silently void the entire leash (`AgentOwnerSame=18`).
- The cooldown arithmetic saturates (`last.saturating_add(interval)`) so an extreme
  owner-set interval can't wrap and silently disable rate limiting.
### Added
- `reject_material(id)` — owner resolves a pending proposal WITHOUT executing it (the
  cleanup path; works while paused). Emits `MaterialRejected`.
- Tests: 39 (bond gating, reopen-after-return, proposal cooldown, reject path,
  role-distinctness, per-validator cap on the redelegate destination, cooldown
  saturation, owner full-exit exemption from the committed bound).
### Removed
- The cargo-odra `Flipper` scaffold (module, Odra.toml entry, CLI bin) and the stale
  livenet deploy bin (deploy goes via the C# SDK).

## [0.5.1] - 2026-06-08
### Security (P6 — the deployed testnet package)
- Agent `undelegate`/`redelegate` bounded by the `committed` accumulator
  (`ExceedsCommitted=15`) — a compromised agent can't grief real positions into
  unbonding; the OWNER's material undelegate stays exempt (full-exit escape hatch).
- `delegate` pre-checks the vault's free balance (`InsufficientFreeBalance`).
- `raise_cap` must actually raise (`CapNotHigher=16`).

## [0.5.0] - 2026-06-06
### Added
- Installer-gated `init` constructor (closes the front-run window on a fresh deploy).
- Per-validator cap (`set_max_per_validator`) backed by an in-contract `committed`
  accumulator; action cooldown (`set_action_interval`); owner kill-switch (`set_paused`).
- Real slashable bond: `deposit_bond` / `slash_bond` (forfeit to owner) / `return_bond`;
  `withdraw` now reserves the bond.
- Ownership + agent recovery: `transfer_ownership`, `set_agent`.
- `redelegate` (move stake validator→validator in one native tx).
- Views for the full leash state so the agent/dashboard read chain-truth gas-free.

### Changed
- Rebuilt around native CSPR staking (delegate/undelegate/redelegate from the vault's own
  purse) — replaced the earlier mock-venue framing. The cap + allowlist are chain-enforced.

## [0.1.0] - 2026-06-05
### Added
- Initial GovernedVault: capped, allowlisted delegation with propose→approve material co-sign.
