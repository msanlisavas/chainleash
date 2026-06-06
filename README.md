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

## Proven on Casper 2.0 testnet

The full leash runs end-to-end on testnet (package
[`8f1d2c7d…879fac`](https://testnet.cspr.live/contract-package/8f1d2c7d7a6d26c58479fcc6be913a77223afdb2bd2dc5664ee58a59c2879fac)).
Selected on-chain artifacts (click to verify):

| What | Transaction |
|------|-------------|
| Agent **autonomously delegates** 500 CSPR from the vault's purse (≤ cap, allowlisted) | [`cf1d7922…`](https://testnet.cspr.live/transaction/cf1d792224182561baa9fe99546ceaa51d8e6d549ef88ff8996653d517a2f63b) |
| Agent **redelegates** 500 CSPR validator→validator in one native tx | [`9989c29f…`](https://testnet.cspr.live/transaction/9989c29f241ca1b023f99518141a8efd50deb98563ac69b475aac972c11f067b) |
| Over-cap delegate (700 > 600 cap) — **rejected on-chain** (`OverCap`) | [`cfe49e71…`](https://testnet.cspr.live/transaction/cfe49e71ad4ff4881a394a083a2b022a15dbe208aa213abada2d2d08ac192a38) |
| Human owner **co-signs** → a material (over-cap) move executes | [`db5dcd2a…`](https://testnet.cspr.live/transaction/db5dcd2a0d73bf453ed824d1523a88ac6cf59688c1be05412f29ce891f8e1a5e) |
| Agent **undelegates** 200 CSPR back to the vault | [`7e14d0bc…`](https://testnet.cspr.live/transaction/7e14d0bce1574617354432c5ca1697520ba93b7fae00c8a553b02c30d9161e21) |
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

- **Contracts** (`contracts/`) — Rust + Odra 2.7: the `GovernedVault` staking leash
  (cap, validator allowlist, propose→approve, posted CSPR bond + on-chain violation
  log, owner-only withdraw).
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
