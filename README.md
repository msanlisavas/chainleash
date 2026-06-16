<p align="center">
  <img src="logo.png" alt="CHAINLEASH" width="160" />
</p>

<h1 align="center">CHAINLEASH</h1>

<p align="center"><em>The trust layer for autonomous money-moving agents on Casper — controlled autonomy the chain enforces, auditable per decision.</em></p>

<p align="center">
  <a href="https://chainleash.ekolsoft.com"><img src="https://img.shields.io/badge/live-chainleash.ekolsoft.com-29c275?style=flat-square" alt="Live demo" /></a>
  <a href="https://github.com/msanlisavas/chainleash/actions/workflows/ci.yml"><img src="https://github.com/msanlisavas/chainleash/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
</p>

<p align="center"><strong>Live on Casper 2.0 testnet → <a href="https://chainleash.ekolsoft.com">chainleash.ekolsoft.com</a></strong> — a real bonded agent rebalancing CSPR under the leash, streamed live.</p>

**CHAINLEASH is the bonded, chain-enforced leash for autonomous money-moving agents on Casper** — an agent that can rebalance, but cannot steal.

It is proven by an AI that governs **native CSPR staking** for institutions holding large CSPR balances — exchanges, custodians, and treasuries. The agent rebalances delegations across validators to execute a published policy, but **what it can do is enforced by Casper itself, not by the server.** The security of the money does not depend on the security of the agent process: a fully compromised agent can mis-delegate within its limits, but it can never steal.

```
funds in contract  ·  per-action cap  ·  validator allowlist  ·  owner-only withdraw
slashable bond  ·  human co-sign on material moves  ·  one-call kill-switch
```

<p align="center">
  <img src="assets/dashboard.png" alt="CHAINLEASH live dashboard" width="820" />
  <br/><em>The live dashboard at <a href="https://chainleash.ekolsoft.com">chainleash.ekolsoft.com</a> — the leash shown as a live ARMED / AWAITING / HALTED instrument, with every agent decision streamed on-chain.</em>
</p>

## The leash: enforced by the chain, not the server

The agent's authority is bounded by the protocol and the contract, not by trust in the server:

- **Funds live in the contract.** The CSPR sits in a deployed Odra **`GovernedVault`** that delegates from its own purse. The agent never custodies it.
- **Value cap.** The agent can only delegate within a per-action value cap — the chain rejects anything over.
- **Validator allowlist.** The agent can only delegate to allowlisted validators — the chain rejects anything else.
- **No agent withdraw path.** Only the human/institution owner can move CSPR out of the vault. A fully compromised agent can mis-delegate within the leash; it can never exfiltrate funds.
- **Human co-sign on material moves.** Over-cap "material" moves require an explicit owner approval (propose → approve).
- **Owner guardrails.** A per-validator cap (no over-concentration), an action cooldown (anti-thrash), and a one-call kill-switch that freezes the agent.
- **Weighted-key authority floor.** The treasury account's native weighted keys place the agent key *below* the `key_management` threshold, so the agent can never expand its own authority or rotate keys. The leash can tighten itself; only a human can loosen it.
- **Slashable bond.** The agent posts a CSPR bond that the owner can forfeit on a logged violation — real economic teeth. This is CHAINLEASH's own mechanism: Casper has no protocol slashing, so the bond is the product's economic backstop, not a chain feature.

## Built on Casper's real on-chain primitive

Casper's deep, native economic primitive today is **staking / delegation**, so that is what the agent governs. A Casper 2.0 contract delegates from its own purse — the auction identifies the delegator by purse, not by public key — which makes the value cap and validator allowlist **fully chain-enforced** rather than advisory. As Casper ships more on-chain venues, the same leash extends to cover them.

## Why it's different

Incumbents (Coinbase Agentic Wallets, AWS Bedrock AgentCore) enforce agent spend limits **off-chain inside an enclave** — the security of the money equals the security of the server. CHAINLEASH makes the limit a **protocol + contract** guarantee: cap, allowlist, and owner-only withdraw are all enforced on-chain.

It pairs that guarantee with an agent that **pays to think.** Before acting, the agent buys a premium risk read over Casper-native **x402** — a real CSPR settlement the provider **verifies on-chain** (recipient, amount, finality) and refuses to accept twice (replay-protected). The read itself is derived from **live validator metrics**, not mock data. And when the policy is satisfied and nothing is off-policy, the agent **chooses not to act at all** — restraint as a first-class behavior.

## How the agent works

Each tick, the agent:

1. **Perceives** the allowlisted validators via live CSPR.cloud metrics (commission, active status, stake).
2. **Scores** them against the **published delegation policy** (max commission, must-be-active).
3. **Pays to think** — if there's an actionable opportunity, it buys a premium risk read over x402.
4. **Acts within the leash** — deploys idle treasury to the lowest-commission compliant validator (routine, ≤ cap); when a delegated validator breaches policy (e.g. a commission hike), it **redelegates** that stake straight to the best compliant validator in one native tx; over-cap or elevated-risk moves are escalated to a human-co-signed proposal.
5. **Or chooses not to act** — restraint as intelligence. When the policy is satisfied and nothing is off-policy, the agent stays put.

Institutions want an agent that *executes a published rule auditably*, not one that exercises opaque discretion — so the policy is deterministic and every decision is recorded on-chain.

## Business model

Primary: **B2B SaaS / licensing** to exchanges, custodians, and institutional treasuries that stake CSPR for users and need auditable, bonded, kill-switchable automation. Later: **basis points on governed (staked) AUM**. Not a data marketplace and not trading fees — validator data is public, so the moat is the *leash*, not the data.

## Works for any CSPR holder — multisig-ready

The same engine serves a 10M-CSPR individual and a 500M-CSPR institution; only the cap, allowlist, bond, and policy parameters differ. Each holder runs **their own vault**, and standing one up is **one command**:

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

That deploys a fresh `GovernedVault`, initializes it (agent + owner + cap), arms the validator allowlist on-chain, optionally funds it and posts the bond, and **points the agent at the new vault** by writing its config — so one config-driven agent can serve many vaults. Manual steps are in the [RUNBOOK](RUNBOOK.md).

**Multisig is native.** The owner can be any Casper account, including a **weighted-key (M-of-N multisig) account** that large wallets already support — so a material move can require, for example, **2-of-3 human signatures**. The contract's owner-gated `approve_material` inherits the owner account's signature threshold directly, with no extra contract logic. CHAINLEASH already relies on this primitive on the *agent* side, where the agent key sits below the account's `key_management` threshold (proven on-chain), so the same weighted-key mechanism secures the owner side too. And because rewards **auto-compound** natively on Casper, simply holding never loses yield; the agent only switches validators when the gain outweighs the unbonding cost.

## Proven on Casper 2.0 testnet

The full leash runs end-to-end on testnet (package
[`612b0776…0758e3`](https://testnet.cspr.live/contract-package/612b07767d7e8245a8a2d2dfd77e56e34776e7be7ecf81b95429b092a30758e3)).
Selected on-chain artifacts (click to verify):

| What | Transaction |
|------|-------------|
| Agent **autonomously delegates** 500 CSPR from the vault's purse (≤ cap, allowlisted) | [`13846b6c…`](https://testnet.cspr.live/transaction/13846b6c373dd48ea497d0f663b178307f7f328a91a39f48ad6e45b6aa5d285a) |
| Agent **redelegates** 400 CSPR validator→validator in one native tx | [`1b192030…`](https://testnet.cspr.live/transaction/1b192030453dcc179cc5bc2d1647a3ae43af75171947fdb1e325f07a39bdfa9c) |
| Over-cap delegate (700 > 600 cap) — **rejected on-chain** (`OverCap`) | [`48daeb16…`](https://testnet.cspr.live/transaction/48daeb16f9c086893e6614a828218ddb93022e033ec56f6e91c6859ec35e9bac) |
| Delegate to a **non-allowlisted** validator — **rejected on-chain** (`ValidatorNotAllowed`) | [`e257c48e…`](https://testnet.cspr.live/transaction/e257c48e5cb598fe92df80383a5846eb76c06856b0ba6aed4e1519c4858cd7bf) |
| Agent tries to **undelegate more than it directed** — **rejected on-chain** (`ExceedsCommitted`, anti-grief) | [`0448eedd…`](https://testnet.cspr.live/transaction/0448eeddc1b6e363bfd2febd1a81bec44d8d4b5f40042073e9463ab15cdee35b) |
| Owner **kill-switch** engaged → agent move **rejected on-chain** (`Paused`) | [`4e7463e5…`](https://testnet.cspr.live/transaction/4e7463e52e34a5dfb25cdcafa23e81a761d5376e7844c9eb6464cf0b24d5e7b7) |
| Human owner **co-signs** → a material (over-cap, 700 CSPR) move executes | [`286bed61…`](https://testnet.cspr.live/transaction/286bed617d866bef1cd58ee95eff9a1341003ac19a0ea32f7d49e362d6fa0a69) |
| Owner **slashes the agent's bond** on a violation (forfeited to owner — real economic teeth) | [`de19786a…`](https://testnet.cspr.live/transaction/de19786adb70a49abddbff3f1c8814285c35837577757b0c701a7db03f1513c1) |
| Agent pays for the premium risk read over **x402** (real CSPR transfer) | [`cd85af4c…`](https://testnet.cspr.live/transaction/cd85af4c07517d353f87ab3a7cfd0243ad11d5b248e117964283f1f815339943) |

### Independently security-reviewed

CHAINLEASH has been through four adversarial multi-agent security reviews — a leash-invariant red-team, a completion audit, and two full security + quality reviews — with every confirmed finding fixed. The reviews independently verified the core guarantee: a fully compromised agent can mis-delegate within the leash but has **no path to move CSPR out of the vault** — every exit is owner-gated, and `withdraw` reserves the bond.

**On-chain hardening:** installer-gated `init` (no front-run window); the per-validator cap and kill-switch are enforced on the material path too; a lag-free in-contract accumulator; the agent's undelegate/redelegate are bounded by what it actually directed (a compromised agent can't even grief positions into unbonding); the bond can only be opened by a principal and topped up by its recorded holder (no one can redirect it); proposals respect the action cooldown and the owner can reject them without executing; the agent and owner roles can never collapse into one key; a genuinely **slashable** and **returnable** bond; and **recoverable** ownership/agent keys.

**Off-chain hardening:** the API locks CORS to the dashboard origin, serves strict security headers (CSP included), rate-limits the public endpoints in two lanes (cheap reads vs. chain-polling co-sign), makes the co-sign confirm single-use and fail-closed (a leaked co-sign tx hash can't forge an audit entry), and keeps the dev server-key fallback off-by-default and fail-closed. The agent's chain reads distinguish "field unset" from "RPC failed", so a rate-limited upstream can never read as *kill-switch off* — the agent holds instead of acting on fabricated state, and the dashboard flags the data as stale.

**Coverage:** 39 contract tests + 59 backend tests + dashboard view-logic specs, with regression coverage for each fix — all gated in CI (including the contract suite). Earlier iterations also demonstrated on-chain autonomous policy-breach exit, non-allowlisted rejection, and a blocked weighted-key over-reach attempt.

## Run it

There are two ways to run, depending on whether you want to **watch** the live vault or **drive your own**.

**1. Observer mode — watch the live demo vault (zero setup):**

```bash
cp .env.example .env        # defaults point at the live demo vault + public testnet key
docker compose up --build
```

With no agent key present, the agent boots **read-only**: it streams the live demo vault's leash state (cap, bond, balances, validators) to the dashboard but signs nothing. Dashboard + API at `http://localhost:5179`, signal provider at `:5080`, and `GET /health` reports chain reachability plus `readOnly: true`. This is the fastest way to see CHAINLEASH live — note the demo vault is bound on-chain to a specific agent key, so **only its owner can drive it**.

**2. Drive your own vault (to see the agent actually act):**

To watch the agent delegate / redelegate / escalate, deploy your **own** vault — its agent key is yours, so the chain accepts its moves:

```bash
# 1) generate keys, fund BOTH at the faucet (~600+ CSPR for the agent: ~500 is install gas)
cd spike/ChainLeash.Spike && dotnet run -- keygen
#    faucet: https://testnet.cspr.live/tools/faucet  (you may need several requests)
# 2) put your CSPR.cloud key in spike/.../Config/settings.local.json, then deploy + arm:
./scripts/onboard.sh --cap 600 --validators <validatorHex> --deposit 1000 --bond 300
#    (Windows: ./scripts/onboard.ps1 -CapCspr 600 -Validators @('<validatorHex>') -DepositCspr 1000 -BondCspr 300)
# 3) run the stack — it now points at YOUR vault
docker compose up --build
```

Runs on Linux, macOS, and Windows — the agent, contracts, dashboard, and `docker compose` are all cross-platform; only the onboarding script has a per-shell variant (`.sh` / `.ps1`).

The agent's audit feed is **persisted** across restarts, and secrets are mounted read-only — never baked into an image. (Local dev without Docker: run `backend/ChainLeash.SignalProvider`, then `backend/ChainLeash.Agent` with `dotnet run`; the agent serves the dashboard from `wwwroot`.) Full details — including the faucet/gas budget — are in the [RUNBOOK](RUNBOOK.md).

**Putting it online for others to watch?** See [Go live (public deploy)](RUNBOOK.md#go-live-public-deploy) — host the agent container behind HTTPS, switch three prod settings, and you're up. It's read-only for visitors by design; only the owner can co-sign or halt.

## Architecture

- **Contracts** (`contracts/`) — Rust + Odra 2.7: the `GovernedVault` staking leash — delegate / undelegate / redelegate under a per-action cap, validator allowlist, per-validator cap, action cooldown, owner kill-switch, propose→approve material co-sign, a posted CSPR bond with on-chain violation log, and owner-only withdraw.
- **Backend** (`backend/`) — .NET 10 + Casper C# SDK: the autonomous agent loop (`AgentWorker`), the perception layer (`ValidatorMonitor`, CSPR.cloud), the on-chain client (`CasperVault`), and the x402 pay-to-think buyer + provider.
- **Frontend** (`frontend/`) — Angular 20 + Tailwind dashboard, designed as an **institutional control console** (graphite/steel palette with red reserved for enforcement, self-hosted IBM Plex). One scrolling page: a hero "leash instrument" that reflects live **ARMED / AWAITING CO-SIGN / HALTED** state, a how-it-works + guarantee explainer, a "Run your own" self-host section, and the live console — the **full leash state read live from chain** (per-action cap, free/total balance, slashable bond, per-validator cap, violations, and a prominent kill-switch banner when the owner pauses the agent), a live audit feed, the validator-policy view with per-validator committed stake, x402 spend, and the human co-sign action. It re-syncs the snapshot on reconnect and surfaces a clear banner if the agent API is unreachable. The owner **co-signs in their own wallet** via CSPR.click: the agent builds the *unsigned* `approve_material` transaction, the owner signs it in Casper Wallet in-browser, and the agent confirms the result on-chain — **the server holds only the owner's public key, never the secret**. (A server-key co-sign path exists for local dev but is **off by default**.)

## Status

🚧 In active development for the **Casper Agentic Buildathon 2026** (submission due 2026-06-30). **Live end-to-end on Casper 2.0 testnet at [chainleash.ekolsoft.com](https://chainleash.ekolsoft.com)** (auto-deployed from `main` via GitHub Actions), independently security-reviewed four times, and covered by 39 contract + 59 backend tests plus dashboard specs — all gated in CI. Built by [@msanlisavas](https://github.com/msanlisavas), maintainer of the Casper MCP Server.

## License

MIT (see `LICENSE`).