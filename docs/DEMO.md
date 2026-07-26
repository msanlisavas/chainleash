# CHAINLEASH — demo-day script (~5 minutes)

A timed beat sheet for the final-round presentation. Every beat is backed by a live
surface or a clickable on-chain artifact, with a fallback if testnet is slow.

## Pre-flight (15 minutes before)

- [ ] Open tabs: [live site](https://chainleash.ekolsoft.com) · [repo README](https://github.com/msanlisavas/chainleash) · the proof-table txs you'll click (over-cap `48daeb16…`, kill-switch `4e7463e5…`, bond slash `de19786a…`, x402 pay `cd85af4c…`, v4 upgrade `b69c73c2…`)
- [ ] Casper Wallet unlocked with the **owner** key; site connected once so the pill reads *owner wallet connected*
- [ ] `GET /health` green; agent gas not low; `ANTHROPIC_API_KEY` set on the server so PAY events carry the model rationale
- [ ] Terminal ready with the MCP one-liner (beat 5) already typed
- [ ] If demoing an agent action live: know the current cooldown + tick cadence so you can predict when it moves

## Beats

| Time | Beat | Say / do | Criterion it evidences | Fallback |
|---|---|---|---|---|
| 0:00 | **The claim** | Hero: "an agent that can rebalance but cannot steal." Point at the ARMED pill — that's live chain state, not a mockup. | UX, working contracts | Screenshot deck |
| 0:40 | **Watch it think** | Scroll to the live console: heartbeat, HOLD/decision stream. Find a PAY event — "the agent paid real CSPR over x402 for that read, and the rationale you see is Claude reasoning over live validator metrics. The model can only tighten the verdict, never loosen it." | AI/agentic, x402 | Persisted audit feed shows the same history |
| 1:30 | **The human in the loop** | As owner: tighten the commission threshold from the policy panel — sign in your own wallet. "The server never holds my key. The agent picks the new policy up next tick and, if a validator now breaches it, redelegates or escalates to co-sign." If a proposal is pending: co-sign it live. | Agentic + UX + contracts | Explain over the policy panel; show a past co-signed tx |
| 2:30 | **The leash bites** | Hit the kill-switch (sign). Site flips to HALTED. "Every agent move now reverts on-chain — error code 11, `Paused`. Not a server flag; the chain itself." Unpause. | Working contracts | Click the historical `Paused` rejection tx |
| 3:15 | **Verify, don't trust** | Proof-table tour, 3 clicks: over-cap rejected (`OverCap`), bond slashed to owner, the v4 in-place upgrade with state + purse preserved. "47 contract, 107 backend, 22 dashboard tests, CI-gated; four adversarial review rounds." | Technical execution | Table is markdown — works offline |
| 4:00 | **Any AI can supervise; none can touch** | Terminal: `claude mcp add chainleash -- dotnet run --project backend/ChainLeash.Mcp`, then ask "Is the vault paused? Prepare the kill-switch for me." Show the returned transaction: `approvals: []` — unsigned. "An AI can *prepare*; only my wallet can *sign*." | AI/agentic, innovation | Show the MCP README + the recorded tool output |
| 4:30 | **Why it matters** | "Incumbents enforce agent limits in an enclave — the money is as safe as the server. We made the limit a protocol guarantee. Same leash extends to RWA treasuries: allowlist→issuers, caps→notional. Mainnet is an audit and a config away. B2B: exchanges, custodians, bps on governed AUM." | Applicability, impact, launch plans | — |

## One-liners to land

- "A fully compromised agent can mis-delegate within its caps. It can never steal."
- "Pay-to-think is literal: that PAY event is CSPR settled on-chain buying Claude inference."
- "The AI can add caution. It cannot remove it."
- "We ship all three of the buildathon's agentic primitives — x402, MCP, and an Agent Skill — plus the one nobody else ships: chain-enforced custody."
