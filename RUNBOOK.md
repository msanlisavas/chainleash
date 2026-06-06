# CHAINLEASH — Runbook

How to build, deploy, and run the full stack, and how to reproduce the on-chain
demo. Everything runs against **Casper 2.0 testnet**.

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` ≥ 10.0.300)
- **Node 20+ and Angular CLI 18** (`ng version`) — for the dashboard
- **Docker** — the Odra/Rust contract toolchain (`casper-types` doesn't host-compile on Windows; we build in a Linux container)
- A **CSPR.cloud** access key (node RPC + REST) — testnet key works
- Two funded testnet keys: an **agent** key and a **human/owner** key (faucet: <https://testnet.cspr.live/tools/faucet>)

Secrets live under `spike/ChainLeash.Spike/secrets/{agent,human}/` and are gitignored.
Put your CSPR.cloud key in `spike/ChainLeash.Spike/Config/settings.local.json` and
`backend/ChainLeash.Agent/appsettings.local.json` (both gitignored).

## 1. Build + test the contract

```bash
docker build -t chainleash-odra tools/odra-build
docker run --rm -v "$PWD/contracts/governed_vault:/work" \
  -v chainleash-cargo-registry:/usr/local/cargo/registry chainleash-odra \
  bash /work/deploy.sh        # cargo test (11/11) + cargo odra build -> wasm/GovernedVault.wasm
```

## 2. Deploy + arm the leash (spike harness)

Run from `spike/ChainLeash.Spike` (`dotnet run -- <cmd>`):

```
vault-deploy                                   # install the staking vault (InstallOrUpgrade, ~500 CSPR gas)
vault-find                                     # read the new package hash from the agent's named keys
vault-init <pkg> 600000000000                  # agent, owner, per-action cap = 600 CSPR
vault-set-validator <pkg> <validatorHex> true  # owner allowlists a validator (repeat per validator)
vault-deposit <pkg> 1000000000000              # fund the vault purse with 1000 CSPR (Odra payable proxy)
```

Pick active validators from CSPR.cloud (`GET /validators?era_id=<current>`); the demo
uses `0106618e…` (3% fee) and `017d96b9…` (5% fee).

## 3. Run the agent + dashboard + x402 provider

```bash
# x402 signal provider (the agent pays it per "think")
dotnet run --project backend/ChainLeash.SignalProvider --urls http://localhost:5080

# agent host + dashboard (build the Angular app into wwwroot first)
cd frontend/dashboard && ng build && cd ../..
dotnet run --project backend/ChainLeash.Agent --urls http://localhost:5179
```

Open <http://localhost:5179> — the dashboard streams every agent decision live.
Set the run knobs in `backend/ChainLeash.Agent/appsettings.local.json`:

```json
{
  "Casper": { "CsprCloudAccessKey": "<key>" },
  "Agent":  { "TickSeconds": 12, "MaxOnChainActions": 2, "MaxTicks": 0 },
  "Staking":{ "TreasuryToDeployCspr": 1000, "NextProposalId": <current on-chain next_id> }
}
```

For the dashboard during development you can instead `ng serve` (port 4200) — it talks
to the agent on `:5179` via CORS.

## 4. Reproduce the demo beats

- **Autonomous delegate** — with idle treasury and a compliant validator, the agent
  pays over x402 and delegates to the lowest-commission validator (≤ cap). Watch the
  `DELEGATE` row appear with a `cspr.live` link.
- **Leash bites** — manually try an over-cap or non-allowlisted move and watch the
  chain reject it: `vault-delegate <pkg> <allowlisted> 700000000000` → `OverCap`;
  `vault-delegate <pkg> <not-allowlisted> 500000000000` → `ValidatorNotAllowed`.
- **Human co-sign** — when the agent escalates (over-cap, or an elevated x402 risk
  read), a material proposal appears on the dashboard. Click **Co-sign (owner)** (or
  `vault-approve <pkg> <id>`) to execute it on-chain.
- **Rebalance on policy breach** — tighten the policy (`MaxCommissionPercent: 4`) so a
  delegated validator at 5% becomes off-policy; the agent undelegates it back to the vault.

## Live deployment + on-chain artifacts

Package `3132a5a7…703f` on testnet. The full artifact list (with transaction hashes)
is in [README.md](README.md#proven-on-casper-20-testnet).

## What stays the agent's, what stays the human's

The agent can rebalance stake **within the cap and allowlist** and propose larger
moves — but it has **no withdraw path** and sits **below the account's
`key_management` threshold**, so it can never move CSPR out of the vault, raise its own
authority, or seize the bond. Only the human owner can. The chain enforces this, not
the server.
