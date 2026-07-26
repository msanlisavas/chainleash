---
name: chainleash-vault
description: Operate, supervise, or onboard a CHAINLEASH GovernedVault — the chain-enforced leash for autonomous CSPR staking agents on Casper. Use when running the stack, deploying a vault, reading vault state, driving the spike CLI, or debugging the agent/dashboard/x402 loop.
---

# Operating the CHAINLEASH leash

> **Install as an Agent Skill:** copy this folder into your project's or home
> `.claude/skills/` directory and Claude Code will load it automatically — or just
> hand the file to any AI assistant as context.

CHAINLEASH is a bonded autonomous staking agent whose authority is enforced **on-chain**
(Casper 2.0 testnet), not by the server. Funds live in a `GovernedVault` contract; the
agent can delegate/undelegate/redelegate within chain-enforced limits and can never
withdraw. This skill teaches you to watch it, drive it, and stand up your own vault.

## The invariants you must never break

- **Custody:** no agent-callable path moves CSPR out of the vault. Only the owner can
  `withdraw` (which reserves the bond), `slash_bond`, or `return_bond`.
- **Guard chain on every agent move:** role → kill-switch → per-action cap → validator
  allowlist → per-validator cap (in-contract `committed` accumulator) → cooldown →
  free-balance. Over-cap moves need a human co-sign (`propose_material` → owner
  `approve_material`).
- **Asymmetric authority:** the agent may only *tighten* its cap; the owner may only
  *raise* it. Owner emergency paths work even while paused.
- Contract error codes (revert as `User error: <code>`): NotInitialized(1) NotAgent(2)
  NotOwner(3) OverCap(4) ValidatorNotAllowed(5) NoSuchProposal(6)
  ProposalAlreadyResolved(7) AlreadyInitialized(8) CapNotLower(9)
  InsufficientFreeBalance(10) Paused(11) PerValidatorCapExceeded(12) RateLimited(13)
  NotInstaller(14) ExceedsCommitted(15) CapNotHigher(16) UnauthorizedBondDeposit(17)
  AgentOwnerSame(18). Odra framework errors are 64536+.

## Fastest path: observer mode (zero setup)

```bash
cp .env.example .env        # defaults point at the live demo vault + public testnet key
docker compose up --build   # dashboard+API :5179, x402 provider :5080
```

The agent boots **read-only** without a key: it streams the live demo vault's leash
state but signs nothing. `GET /health` reports chain reachability + `readOnly: true`.

## Read the live system (no auth, works against https://chainleash.ekolsoft.com too)

| Endpoint | What it returns |
|---|---|
| `GET /api/state` | Full leash state (paused, cap, balances, bond, violations) + audit events |
| `GET /api/staking` | Per-validator positions and rewards (contract-purse view) |
| `GET /api/validators` | Validator directory (name, key, commission, active) |
| `GET /health` | Chain reachability, agent gas, low-gas flag |

An MCP server wrapping these lives in `backend/ChainLeash.Mcp` — point any MCP client at
it to supervise the vault conversationally (see its README).

## Drive your own vault

1. `cd spike/ChainLeash.Spike && dotnet run -- keygen` — then fund both keys at
   https://testnet.cspr.live/tools/faucet (agent needs **~600+ CSPR**, owner ~50+; one
   faucet drip is usually not enough — request several).
2. Put a CSPR.cloud key in `spike/ChainLeash.Spike/Config/settings.local.json`.
3. Onboard (deploys, initializes, arms the allowlist, funds, bonds, writes agent config):
   - Linux/macOS: `./scripts/onboard.sh --cap 600 --validators <hex>[,<hex>] --deposit 1000 --bond 300`
   - Windows: `./scripts/onboard.ps1 -CapCspr 600 -Validators @('<hex>') -DepositCspr 1000 -BondCspr 300`
4. `docker compose up --build`

**Sharp edges (read before debugging):**
- **The `.env` trap:** onboarding writes `backend/ChainLeash.Agent/appsettings.local.json`,
  but that file is **dockerignored** — the compose agent reads `VAULT_PKG`, `OWNER_PUBKEY`
  and the x402 trio (`X402_PAY_TO`, `X402_PROVIDER_PUBKEY`, `X402_EXPECTED_PAYER`) from
  `.env` only. After onboarding, copy those values into `.env` or the agent will sign
  against the demo vault, which rejects it.
- **Node 24 required** for the dashboard (`npm ci` under npm 10/Node 20–22 fails with a
  bogus "missing from lock file" error — the lockfile is npm 11).
- **`onboard.ps1` needs PowerShell 7+ (`pwsh`)** when re-running with an existing
  `appsettings.local.json` (`ConvertFrom-Json -AsHashtable` doesn't exist in 5.1).
- **Contract builds only in the Linux container** (`tools/odra-build`) — `casper-types`
  doesn't host-compile on Windows. The committed `wasm/GovernedVault.wasm` means
  onboarding never needs the Rust toolchain.
- The spike CLI **exits 0 even on on-chain rejection** — check output for
  `IsSuccess=True` (the onboard scripts do this for you).

Spike commands (`dotnet run -- <cmd>` from `spike/ChainLeash.Spike`): `keygen`,
`account-hash`, `vault-deploy`, `vault-find`, `vault-init <pkg> <capMotes>`,
`vault-set-validator <pkg> <hex> true|false`, `vault-deposit <pkg> <motes>`,
`vault-bond <pkg> <motes>`, `vault-delegate <pkg> <hex> <motes>`,
`vault-approve <pkg> <id>`, `vault-reject <pkg> <id>`, `vault-pause <pkg> true|false`,
`vault-set-commission <pkg> <pct>`, `vault-clear-committed <pkg> <hex>`.

## Tests

- Backend: `dotnet test backend/ChainLeash.Tests` (107)
- Dashboard: `cd frontend/dashboard && npm run test:ci` (22, needs Node 24 + Chrome)
- Contract: 47 tests, run in the `chainleash-odra` container (see RUNBOOK §1) or CI.

## Safety rules for agents operating this repo

- Never commit secrets (`spike/**/secrets/`, `.env`, `appsettings.local.json` are
  gitignored — keep it that way).
- Pushing to `main` **auto-deploys** the live site (deploy.yml); markdown and
  `.github/**` changes are paths-ignored. Build + test locally before pushing code.
- The live demo vault is bound on-chain to its agent key — only its owner can drive it;
  don't try to make the demo agent act from a fresh clone (that's what your own vault
  is for).
