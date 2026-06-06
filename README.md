<p align="center">
  <img src="logo.png" alt="CHAINLEASH" width="160" />
</p>

<h1 align="center">CHAINLEASH</h1>

<p align="center"><em>Controlled autonomy the chain enforces — auditable per decision.</em></p>

CHAINLEASH is the **bonded, chain-enforced leash for autonomous money-moving agents
on Casper** — proven by an AI that governs **native CSPR staking** for institutions
that hold large CSPR balances (exchanges, custodians, treasuries). The agent
rebalances delegations across validators to enforce a published policy, but **what
it can do is enforced by Casper itself — not by the server**:

- the funds live in a deployed Odra **`GovernedVault`** contract that delegates them;
- the agent can only delegate **within a per-action value cap**, and only to
  **allowlisted validators** — the chain rejects anything else;
- **the agent has no withdraw path** — only the human/institution owner can move
  CSPR out of the vault. A fully compromised agent can mis-delegate within the
  leash; it can never steal;
- over-cap "material" moves require an explicit **human co-sign** (propose → approve);
- the owner can set a **per-validator cap** (no over-concentration → decentralization), an
  **action cooldown** (anti-thrash), and a one-call **kill-switch** that freezes the agent;
- the treasury account's **native weighted keys** put the agent key *below* the
  `key_management` threshold, so the agent can never expand its own authority or
  rotate keys. The leash can tighten itself; only a human loosens it.

This maps directly to the Casper Manifest's thesis: **the trust layer for the agent economy.**

<p align="center">
  <img src="assets/dashboard.png" alt="CHAINLEASH live dashboard" width="820" />
  <br/><em>The live dashboard — every agent decision streamed on-chain, with the leash enforced by the Casper contract.</em>
</p>

## Built only on what Casper actually has

Casper's real, deep economic primitive today is **native staking / delegation** — so
that is what the agent governs. No fictional DeFi venue, no bridged T-bills: a Casper
2.0 contract delegates from its own purse (the auction identifies the delegator by
purse, not public key), which makes the value cap and allowlist **fully chain-enforced**.
As Casper ships more on-chain venues, the same leash extends to them.

## Why it's different

Incumbents (Coinbase Agentic Wallets, AWS Bedrock AgentCore) enforce agent spend
limits **off-chain inside an enclave** — the security of the money equals the security
of the server. CHAINLEASH makes the limit a **protocol + contract** guarantee
(cap + allowlist + owner-only withdraw, all chain-enforced), and pairs it with an
agent that **pays to think**: before acting it buys a
premium risk read over Casper-native **x402** (a real CSPR settlement) and, when the
policy is satisfied and nothing is off-policy, it **chooses not to act at all**.

## Business model

Primary: **B2B SaaS / licensing** to exchanges, custodians, and institutional treasuries
that stake CSPR for users and need auditable, bonded, kill-switchable automation. Later:
**basis points on governed (staked) AUM**. Not a data marketplace and not trading fees —
validator data is public, so the moat is the *leash*, not the data.

## Works for any CSPR holder — multisig-ready

The same engine serves a 10M-CSPR individual and a 500M-CSPR institution; only the
cap, allowlist, bond, and policy params differ. Each holder runs **their own vault**:
deploy a `GovernedVault`, set themselves (or their multisig) as owner, choose the
allowlist + cap + per-validator cap + cooldown, fund it, and point the agent at it
(the agent is config-driven, so one agent can serve many vaults). Steps are in the
[RUNBOOK](RUNBOOK.md).

**Multisig is native.** The owner can be any Casper account, including a **weighted-key
(M-of-N multisig) account** that big wallets already support — so a material move can
require, say, **2-of-3 human signatures** (the contract's owner-gated `approve_material`
inherits the owner account's signature threshold; no extra contract logic). CHAINLEASH
already uses this primitive on the *agent* side — the agent key sits below the account's
`key_management` threshold, proven on-chain — so the same weighted-key mechanism secures
the owner side too. And rewards **auto-compound** natively, so simply holding never loses
yield; the agent only switches validators when the gain outweighs the unbonding cost.

## Proven on Casper 2.0 testnet

The full leash runs end-to-end on testnet (package
[`1a34ca6c…87d1`](https://testnet.cspr.live/contract-package/1a34ca6c151d8747d88337740ca0f70b1b31cde097dbacebed4c671d8c9b87d1)).
Selected on-chain artifacts (click to verify):

| What | Transaction |
|------|-------------|
| Agent **autonomously delegates** 500 CSPR from the vault's purse (≤ cap, allowlisted) | [`b02bcc74…`](https://testnet.cspr.live/transaction/b02bcc74ce981b0aedc4e0e0fb879636b91ac84c7735a602ab5cb97f87e6b55f) |
| Agent **redelegates** 500 CSPR validator→validator in one native tx | [`54f60083…`](https://testnet.cspr.live/transaction/54f60083f5b0722f9810b83e090ad751edd145eb3002ec35eb5f2ea75ac3f185) |
| Over-cap delegate (700 > 600 cap) — **rejected on-chain** (`OverCap`) | [`1e98c04b…`](https://testnet.cspr.live/transaction/1e98c04b1cbec64452cdba9106034712b391968e7b2cf8ea49452f58d6f802e7) |
| Over per-validator cap — **rejected on-chain** (`PerValidatorCapExceeded`, decentralization) | [`fa2e43ab…`](https://testnet.cspr.live/transaction/fa2e43abc1037b31367cfe09cc2be85072b621c38943d727ff8dc32223caa4a7) |
| Owner **kill-switch** engaged → agent move **rejected on-chain** (`Paused`) | [`62d0bebb…`](https://testnet.cspr.live/transaction/62d0bebbf2cb8909642fb9b3bc836af83e432c839b75f18b57be46a984c49989) |
| Human owner **co-signs** → a material (over-cap) move executes | [`0913698d…`](https://testnet.cspr.live/transaction/0913698d355607e29e9a2a4886b212d4caeac587fb6abeaef9805ad05f9765aa) |
| Agent **undelegates** 200 CSPR back to the vault | [`7ddcc527…`](https://testnet.cspr.live/transaction/7ddcc5278e11a4304bc1617332a527cb482ae42a5b31970f9f09cf01c7cc2c66) |
| Agent pays for the premium risk read over **x402** (real CSPR transfer) | [`cd85af4c…`](https://testnet.cspr.live/transaction/cd85af4c07517d353f87ab3a7cfd0243ad11d5b248e117964283f1f815339943) |

Earlier artifacts (autonomous policy-breach exit, non-allowlisted rejection, weighted-key
over-reach) are catalogued in the deploy notes.

## How the agent works

Each tick the agent:
1. **perceives** the allowlisted validators via live CSPR.cloud metrics (commission,
   active status, stake);
2. scores them against the **published delegation policy** (max commission, must-be-active);
3. if there's an actionable opportunity, **pays over x402** for a premium risk read;
4. **acts within the leash** — deploys idle treasury to the lowest-commission compliant
   validator (routine, ≤ cap); when a delegated validator breaches policy (e.g. a
   commission hike) it **redelegates** that stake straight to the best compliant
   validator in one native tx; over-cap or elevated-risk moves are escalated to a
   human-co-signed proposal;
5. otherwise **chooses not to act** — restraint as intelligence.

Institutions want the agent to *execute a published rule auditably*, not to exercise
opaque discretion — so the policy is deterministic and every decision is on-chain.

## Architecture

- **Contracts** (`contracts/`) — Rust + Odra 2.7: the `GovernedVault` staking leash —
  delegate/undelegate/redelegate under a per-action cap, validator allowlist,
  per-validator cap, action cooldown, owner kill-switch, propose→approve material
  co-sign, a posted CSPR bond + on-chain violation log, and owner-only withdraw.
- **Backend** (`backend/`) — .NET 10 + Casper C# SDK: the autonomous agent loop
  (`AgentWorker`), the perception layer (`ValidatorMonitor`, CSPR.cloud), the on-chain
  client (`CasperVault`), and the x402 pay-to-think buyer + provider.
- **Frontend** (`frontend/`) — Angular dashboard: live audit feed, validator-policy
  view, x402 spend, and a human co-sign action for material proposals (today the owner
  key signs `approve_material` server-side; in-browser CSPR.click / Casper Wallet
  signing is the planned hardening).

## Status

🚧 In active development for the **Casper Agentic Buildathon 2026** (submission due
2026-06-30). Built by [@msanlisavas](https://github.com/msanlisavas), maintainer of the
Casper MCP Server.

## License

MIT (see `LICENSE`).
