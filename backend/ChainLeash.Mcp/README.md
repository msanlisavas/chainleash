# Point your AI at the leash

A standalone **MCP (Model Context Protocol) server** for CHAINLEASH. Any MCP client —
Claude Desktop, Claude Code, or anything else that speaks MCP over stdio — can supervise
the **live on-chain staking vault**: read the leash state, audit the agent's decisions,
inspect staking positions, check the co-sign queue, and even *prepare* owner transactions.

It wraps the project's public **read-only** HTTPS API (`https://chainleash.ekolsoft.com`).
The one write-shaped tool, `prepare_owner_action`, only ever returns an **unsigned**
TransactionV1 — an AI agent can PREPARE but can never SIGN. That is the leash.

## Quick start

Requires the .NET 10 SDK.

### Claude Code

```
claude mcp add chainleash -- dotnet run --project backend/ChainLeash.Mcp
```

(Run from the repo root, or use the absolute path to `backend/ChainLeash.Mcp`.)

### Claude Desktop

Add to `claude_desktop_config.json` (Windows: `%APPDATA%\Claude\claude_desktop_config.json`,
macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "chainleash": {
      "command": "dotnet",
      "args": ["run", "--project", "<absolute-path-to-repo>/backend/ChainLeash.Mcp"]
    }
  }
}
```

(Adjust the path to wherever you cloned the repo.)

## Configuration

| Env var | Default | Purpose |
|---|---|---|
| `CHAINLEASH_API` | `https://chainleash.ekolsoft.com` | Base URL of the CHAINLEASH agent API (point it at `http://localhost:5000` for a local run). |

All HTTP calls share a 10-second timeout; any failure comes back as a one-line error
string instead of a protocol error.

## Tools

| Tool | What it does |
|---|---|
| `get_vault_state` | Full live leash + agent state (paused, caps, cooldown, balances, bond, violations, validators, proposals) plus the ~30 most recent audit events. |
| `get_staking_positions` | Where the vault delegated and what it earned: per-validator principal / current stake / reward / status, plus portfolio totals. |
| `get_validators` | The public validator directory: key, name, commission %, active — for vetting allowlist candidates. |
| `get_health` | Ops health: chain reachability, observer mode, x402 enabled, stale/paused flags, agent gas (low-gas warning), vault balance. |
| `get_pending_proposals` | Only the unresolved material (over-cap) proposals awaiting the owner's in-wallet co-sign. |
| `explain_leash` | Static: roles, the on-chain guard chain, the custody invariant, and the contract error-code table (1–18). |
| `prepare_owner_action` | Builds the **UNSIGNED** owner transaction (pause/unpause, withdraw, undelegate, redelegate, reject, clearcommitted, raisecap, setmaxval, setcooldown, setvalidator, setcommission) for the owner to sign in their own wallet. |

## Example prompts

- "Is the vault paused and what's the current cap?"
- "What did the agent do in the last hour and why?"
- "Which allowlisted validators currently breach the commission policy?"
- "Prepare the kill-switch transaction for me to sign."

## Why this can't go wrong

Every tool is a read against a public API — except `prepare_owner_action`, which returns a
transaction **nobody but the owner's wallet can sign**, because every owner entry point is
owner-gated on-chain. The chain enforces the leash; this server just gives your AI a
window onto it.
