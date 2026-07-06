# Contributing

Thanks for your interest in CHAINLEASH — a bonded, protocol-governed autonomous staking agent
on Casper, made of a Rust/Odra contract, a .NET 10 backend, and an Angular dashboard.

## Build & test

See [`RUNBOOK.md`](RUNBOOK.md) for the full walkthrough. In short:

- **Contract (Rust / Odra)** — `casper-types` doesn't host-compile on Windows, so contract
  build/test runs on Linux. `cargo test` in `contracts/governed_vault` (or the `chainleash-odra`
  container) runs the suite.
- **Backend (.NET 10)** — `dotnet test backend/ChainLeash.Tests`.
- **Dashboard (Angular)** — `cd frontend/dashboard && npm ci && npm run test:ci`
  (`npx ng build` also AOT-typechecks every template binding).

CI runs all three suites plus CodeQL on every push and PR.

## Ground rules

- **Keep the leash intact.** The agent must never gain a path to move CSPR out of the vault —
  that custody guarantee is the whole point. Changes that touch the contract's authority model
  should say how they preserve it.
- **Test every behavioural change.** The pure decision logic lives in `StakingPolicy` (backend)
  and the contract's own test module — put new rules there so they can be tested in isolation.
- **Match the surrounding code** — its style, naming, and comment density.

## Pull requests

Open a PR against `main` with a clear description of what changed and why; the PR template
prompts for the essentials. Run the relevant test suite before you push.
