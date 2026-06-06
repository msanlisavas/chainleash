# Changelog

Changelog for `governed_vault` — the on-chain leash for CHAINLEASH.

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
