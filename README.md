<p align="center">
  <img src="logo.png" alt="CHAINLEASH" width="160" />
</p>

<h1 align="center">CHAINLEASH</h1>

<p align="center"><em>Controlled autonomy the chain enforces — auditable per decision.</em></p>

CHAINLEASH is a bonded, protocol-governed autonomous **AI treasury agent** on the
Casper Network. An AI agent manages a tokenized-T-bill treasury under spending
limits enforced by **Casper's protocol and a deployed Odra contract — not by the
server**. The agent posts a slashable CSPR bond, pays per decision over **x402**
to decide whether acting is even worth it (and frequently chooses *not* to act),
and produces a per-decision, cryptographic audit trail. Over-limit moves are
physically impossible for any compromised backend to push through; a human
co-signs the big ones with a phone tap.

This maps directly to the Casper Manifest's thesis: **the trust layer for the
agent economy.**

## Why it's different

Incumbents (Coinbase Agentic Wallets, AWS Bedrock AgentCore) enforce agent spend
limits **off-chain inside an enclave** — so the security of the money equals the
security of the server. CHAINLEASH makes the limit a **protocol + contract +
economic-bond** guarantee:

- **Native weighted keys** mean the agent key sits *below* the account's
  `key_management` threshold — the agent can never expand its own authority,
  rotate keys, or seize its bond. The leash can tighten itself; only a human can
  loosen it.
- An **Odra `GovernedVault`** enforces a per-action value cap + counterparty
  allowlist, and a propose → human-approve flow for material moves.
- A **slashable bond** gives the agent skin in the game; violations are recorded
  on-chain.

## Architecture

- **Contracts** (`contracts/`) — Rust + Odra: `GovernedVault`, CEP-18 `TBILL`.
- **Backend** (`backend/`) — ABP / .NET 10: agent background workers (EV/LLM loop
  + overseer), Casper C# SDK, Casper MCP client, CSPR.cloud streaming, x402 client.
- **Frontend** (`frontend/`) — Angular dashboard: live audit feed, EV-math panel,
  CSPR.click human co-sign, clickable `cspr.live` deploy hashes.

## Status

🚧 In active development for the **Casper Agentic Buildathon 2026** (submission due
2026-06-30). Built by [@msanlisavas](https://github.com/msanlisavas), maintainer
of the Casper MCP Server.

## License

MIT (see `LICENSE`).
