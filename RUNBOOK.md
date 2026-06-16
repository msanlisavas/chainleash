# CHAINLEASH — Runbook

How to build, deploy, run, and test the full stack, and how to reproduce the on-chain
demo. Everything runs against **Casper 2.0 testnet**.

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` ≥ 10.0.300)
- **Node 20+ and Angular CLI 20** (`ng version`) — for the dashboard
- **Docker** — runs the full stack, and the Odra/Rust contract toolchain (`casper-types`
  doesn't host-compile on Windows; we build it in a Linux container)
- A **CSPR.cloud** access key (node RPC + REST) — the public testnet key works for dev
- Two funded testnet keys: an **agent** key and a **human/owner** key (faucet:
  <https://testnet.cspr.live/tools/faucet>). Budget to deploy + run your own vault:
  the **agent** needs **~600+ CSPR** (≈500 is the install-gas ceiling, plus per-call gas
  and what you deposit/bond) and the **owner** ~50+ CSPR for co-sign gas. A single faucet
  drip may not cover the install — request a few times. (Gas limits are ceilings; Casper
  2.0 only charges what's actually consumed, but you must *hold* the limit to submit.)
- For the in-wallet co-sign: **Casper Wallet** with the owner key imported, and
  (optionally) a **CSPR.click** appId from <https://console.cspr.click> (the public
  `csprclick-template` appId works for local testing)

Secrets live under `spike/ChainLeash.Spike/secrets/{agent,human}/` and are gitignored.
Put your CSPR.cloud key in `spike/ChainLeash.Spike/Config/settings.local.json` and
`backend/ChainLeash.Agent/appsettings.local.json` (both gitignored).

## Quick start (Docker)

The fastest way to run the whole stack against the live demo vault:

```bash
cp .env.example .env        # set CSPR_CLOUD_KEY (defaults already point at the demo vault)
docker compose up --build
```

- Dashboard + API: <http://localhost:5179>
- Health/ops: <http://localhost:5179/health> (chain reachability, agent gas, low-gas flag)
- x402 signal provider: <http://localhost:5080>

Secrets are mounted read-only; the audit feed persists in a named volume.

**Upgrading an existing deployment:** the containers now run as a non-root user. A
`chainleash-data` volume created by an older (root-running) image is root-owned, which
silently breaks feed persistence (the agent logs a one-time warning). Reset it once with
`docker compose down && docker volume rm chainleash-data` — the feed restarts empty; the
leash state is always re-read from chain anyway. On native Linux, if you ran observer
mode before `keygen`, Docker may have created `spike/ChainLeash.Spike/secrets/agent` as
root — `sudo chown -R $USER spike/ChainLeash.Spike/secrets` before generating keys.

**Driving your own vault?** Also point the x402 pair at YOUR keys in `.env`
(`X402_PAY_TO`, `X402_PROVIDER_PUBKEY`, `X402_EXPECTED_PAYER` — see `.env.example`),
or the PAY beat stays bound to the demo identities and the provider will refuse your
agent's payments.

## 1. Build + test the contract

```bash
docker build -t chainleash-odra tools/odra-build
docker run --rm -v "$PWD/contracts/governed_vault:/work" \
  -v chainleash-cargo-registry:/usr/local/cargo/registry chainleash-odra \
  bash /work/deploy.sh        # cargo test (39/39) + cargo odra build -> wasm/GovernedVault.wasm
```

## 2. Deploy + arm your own vault — one command

```bash
# Linux / macOS
./scripts/onboard.sh --cap 600 \
  --validators 0106618e1493f73ee0bc67ffbad4ba4e3863b995d61786d9b9a68ec7676f697981,017d96b9a63abcb61c870a4f55187a0a7ac24096bdb5fc585c12a686a4d892009e \
  --deposit 1000 --bond 300
```
```powershell
# Windows (PowerShell)
./scripts/onboard.ps1 -CapCspr 600 `
  -Validators @('0106618e1493f73ee0bc67ffbad4ba4e3863b995d61786d9b9a68ec7676f697981',
                '017d96b9a63abcb61c870a4f55187a0a7ac24096bdb5fc585c12a686a4d892009e') `
  -DepositCspr 1000 -BondCspr 300
```

This deploys a fresh `GovernedVault`, initializes it (agent + owner + cap), arms the
validator allowlist on-chain, optionally funds it and posts the bond, then writes the
new package hash + allowlist into `backend/ChainLeash.Agent/appsettings.local.json` so
the agent points at it. Pick active validators from CSPR.cloud
(`GET /validators?era_id=<current>`).

### Manual equivalent (spike harness)

Run from `spike/ChainLeash.Spike` (`dotnet run -- <cmd>`):

```
vault-deploy                                   # install the staking vault (InstallOrUpgrade, ~500 CSPR gas)
vault-find                                     # read the new package hash from the agent's named keys
vault-init <pkg> 600000000000                  # agent, owner, per-action cap = 600 CSPR
vault-set-validator <pkg> <validatorHex> true  # owner allowlists a validator (repeat per validator)
vault-deposit <pkg> 1000000000000              # fund the vault purse with 1000 CSPR (Odra payable proxy)
vault-bond <pkg> 300000000000                  # agent posts a 300 CSPR slashable bond
```

## 3. Run the agent + dashboard + x402 provider (without Docker)

```bash
# x402 signal provider (the agent pays it per "think")
dotnet run --project backend/ChainLeash.SignalProvider --urls http://localhost:5080

# agent host + dashboard (build the Angular app into wwwroot first)
cd frontend/dashboard && ng build && cd ../..
dotnet run --project backend/ChainLeash.Agent --urls http://localhost:5179
```

Open <http://localhost:5179> — the dashboard streams every agent decision live and shows
the full leash state read from chain. For dashboard development you can instead `ng serve`
(port 4200) — it talks to the agent on `:5179` via CORS.

> ⚠️ **`ng serve` is the Angular dev server — LOCAL DEVELOPMENT ONLY.** Never run it on a
> server, and never expose it to a network (don't pass `--host`/`--disable-host-check`). It
> binds `localhost` by default (pinned in `angular.json`) — keep it that way. In production the
> **agent** serves the prebuilt static bundle from `wwwroot`; nothing runs the dev server. See
> "Go live" below.

### Config reference (`backend/ChainLeash.Agent/appsettings.local.json`, gitignored)

The leash state (cap, bond, per-validator cap, balances, paused, violations) is read
**from chain** — not config. Config only holds connection, keys, policy, and run knobs:

```json
{
  "Casper": {
    "CsprCloudAccessKey": "<key>",
    "GovernedVaultPackageHash": "hash-<your vault>",
    "OwnerPublicKeyHex": "<owner public key>",
    "AllowServerKeyCoSign": false
  },
  "Agent":   { "TickSeconds": 12, "MaxOnChainActions": 2, "LowGasWarnCspr": 50 },
  "Staking": { "Allowlist": ["<validatorHex>", "..."], "MaxCommissionPercent": 6, "DeployChunkCspr": 500, "BondCspr": 300 },
  "Dashboard": { "CsprClickAppId": "<your CSPR.click appId>" }
}
```

**Multi-tenant:** the agent is config-driven, so one agent process serves one vault per
config; run several (or several configs) to manage many vaults. `scripts/onboard.ps1`
writes a fresh config per vault.

## Go live (public deploy)

To put your instance on the internet for others to watch, you host the **agent
container** — it serves the dashboard, the API and the SignalR feed *and* runs the agent
worker, so this is a long-lived process, not static hosting. Any always-on host with
Docker works: a small VPS (`docker compose up -d`), or a container platform (Fly.io,
Render, Railway, Azure Container Apps).

> ⚠️ **Serve the built bundle, not the dev server.** "Going live" hosts the **agent**, which
> serves the *prebuilt static* dashboard from `wwwroot`. Do **not** run `ng serve` (the Angular
> dev server) on a server or point your reverse proxy at it — it's a local-dev tool, never a
> production server, and exposing it has carried dev-server CVEs (path-traversal / source
> disclosure). Every step below exposes the **agent** (port 5179), not the dev server.

**1. Three prod config changes** (the local-dev defaults are wrong for prod):

| Setting | Dev | Prod | Why |
|---|---|---|---|
| `Agent:MaxTicks` | `2` (in `appsettings.local.json`) | `0` | non-zero stops the host after N ticks; `0` = run forever |
| `Casper:CsprCloudAccessKey` | shared public key | **your own** (`console.cspr.cloud`) | the public key is daily-rate-limited → the dashboard goes "chain read stale" under traffic |
| `Dashboard:CsprClickAppId` | `csprclick-template` | **your own** (`console.cspr.click`) | reliable, branded wallet co-sign |

**2. Expose it + TLS.** Set `BIND_ADDR=0.0.0.0` in `.env`, then put the agent behind
**HTTPS** — wallet extensions / CSPR.click only sign in a secure context, and you want TLS
for a public site. Simplest is Caddy (auto-HTTPS) or a Cloudflare Tunnel:

```caddy
chainleash.example.com {
    reverse_proxy 127.0.0.1:5179   # the agent serves the dashboard + API + SignalR
}
```

The dashboard is served **same-origin** as the API, so there's no CORS to configure. The
CSP already allows `cdn.cspr.click`, `wss:` and `https:`.

**3. Bring it up.**

```bash
cp .env.example .env     # set CSPR_CLOUD_KEY (+ VAULT_PKG / OWNER_PUBKEY for your vault)
docker compose up -d --build
curl -s localhost:5179/health   # 200 = chain reachable; reports agentGasCspr + lowGas
```

**Public exposure is already safe** — per-IP rate limits (api 60/min, cosign 10/min),
secrets mounted read-only (never baked), non-root containers + healthchecks +
`restart: unless-stopped`, and `/confirm` is single-use and on-chain-verified. A visitor
can only **read**; they can't co-sign (not the owner) or move funds (the leash). The one
ongoing task is **agent gas**: `/health` flags `lowGas`; top up from the
[faucet](https://testnet.cspr.live/tools/faucet) when it dips.

> Read-only by design: the public watches a real, bonded agent operate under the leash and
> can verify every action on the explorer. The owner controls (co-sign, kill-switch) are
> gated to the owner's wallet — demo those yourself by connecting your owner key.

## 4. Reproduce the demo beats

- **Autonomous delegate** — with idle treasury and a compliant validator, the agent
  pays over x402 and delegates to the lowest-commission validator (≤ cap). Watch the
  `DELEGATE` row appear with a `cspr.live` link.
- **Leash bites** — manually try an over-cap or non-allowlisted move and watch the
  chain reject it: `vault-delegate <pkg> <allowlisted> 700000000000` → `OverCap`;
  `vault-delegate <pkg> <not-allowlisted> 500000000000` → `ValidatorNotAllowed`.
- **In-wallet human co-sign** — when the agent escalates (over-cap, or an elevated x402
  risk read), a material proposal appears on the dashboard. Connect the **owner wallet**
  and click **Co-sign in wallet (owner)**: the agent hands your Casper Wallet the unsigned
  `approve_material` tx, you sign in-browser, and the agent confirms it on-chain. (CLI
  equivalent: `vault-approve <pkg> <id>`. The dashboard's `dev: server-key co-sign` button
  appears only if `Casper:AllowServerKeyCoSign` is enabled.)
- **Rebalance on policy breach** — tighten the policy (`MaxCommissionPercent: 4`) so a
  delegated validator at 5% becomes off-policy; the agent **redelegates** its stake to the
  best compliant validator in one native tx (or undelegates if none qualify).
- **Kill-switch** — `vault-pause <pkg> true` → the dashboard shows a red banner and every
  agent move is rejected on-chain (`Paused`) until the owner unpauses.

## 5. Tests

```bash
dotnet test backend/ChainLeash.Tests/ChainLeash.Tests.csproj   # leash policy + decoders + co-sign verifier (59)
cd frontend/dashboard && npm run test:ci                       # dashboard view-logic (headless)
```

CI (`.github/workflows/ci.yml`) runs both on every push/PR.

## Live deployment + on-chain artifacts

Package
[`612b0776…0758e3`](https://testnet.cspr.live/contract-package/612b07767d7e8245a8a2d2dfd77e56e34776e7be7ecf81b95429b092a30758e3)
on testnet (upgradable). The full artifact list (with transaction hashes) is in
[README.md](README.md#proven-on-casper-20-testnet).

## What stays the agent's, what stays the human's

The agent can rebalance stake **within the cap and allowlist** and propose larger
moves — but it has **no withdraw path** and sits **below the account's
`key_management` threshold**, so it can never move CSPR out of the vault, raise its own
authority, or rotate keys. Only the human owner can, and material moves are co-signed in
the owner's **own wallet** — the server never holds the owner key. The chain enforces
this, not the server.
