using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace ChainLeash.Mcp;

/// <summary>
/// MCP tools wrapping the CHAINLEASH read-only HTTPS API. Every tool returns a string —
/// raw JSON where the shape is already LLM-friendly, or a clear one-line error. Nothing
/// here ever throws to the client, and nothing here can sign or submit a transaction.
/// </summary>
[McpServerToolType]
public sealed class ChainLeashTools(HttpClient http)
{
    private const int MaxAuditEvents = 30;

    // ────────────────────────────── read-only vault views ──────────────────────────────

    [McpServerTool(Name = "get_vault_state"), Description(
        "Full live leash + agent state of the CHAINLEASH governed vault: kill-switch (paused), " +
        "per-action cap (capCspr), per-validator cap, cooldown, commission threshold, balances " +
        "(total/free/bond), violations, watched validators with policy verdicts, pending material " +
        "proposals, plus the most recent audit events (the agent's decision trail: PERCEIVE/PAY/" +
        "DELEGATE/HOLD/PROPOSE/OWNER...). Amounts are CSPR.")]
    public async Task<string> GetVaultState()
    {
        var (ok, body) = await Get("/api/state");
        if (!ok) return body;
        try
        {
            var root = JsonNode.Parse(body);
            if (root?["events"] is JsonArray events && events.Count > MaxAuditEvents)
            {
                var trimmed = new JsonArray();
                for (var i = 0; i < MaxAuditEvents; i++)
                    trimmed.Add(events[i]!.DeepClone());
                root["events"] = trimmed;
                root["eventsNote"] = $"audit trail trimmed to the {MaxAuditEvents} most recent events (newest first)";
            }
            return root?.ToJsonString() ?? body;
        }
        catch (JsonException)
        {
            return body; // unparseable? still give the caller what the API said
        }
    }

    [McpServerTool(Name = "get_staking_positions"), Description(
        "Where the vault's CSPR is delegated and what it earned: per-validator positions " +
        "(principal from the contract's committed ledger, current stake from the chain index, " +
        "reward = current − principal, status e.g. Delegated / Settling / Exit pending) plus " +
        "portfolio totals, free balance, bond, and total under management. Amounts are CSPR.")]
    public async Task<string> GetStakingPositions()
    {
        var (_, body) = await Get("/api/staking");
        return body;
    }

    [McpServerTool(Name = "get_validators"), Description(
        "The public validator directory (top validators by stake): public key, registered " +
        "branding name, commission fee percent, and whether the validator is active in the " +
        "current era. Useful to vet candidates before asking the owner to allowlist one.")]
    public async Task<string> GetValidators()
    {
        var (_, body) = await Get("/api/validators");
        return body;
    }

    [McpServerTool(Name = "get_health"), Description(
        "Operational health of the agent + chain: status ok/degraded, chain reachability, " +
        "observer/read-only mode, x402 pay-to-think enabled, stale flag, paused flag, the " +
        "agent's gas balance (with low-gas warning), and the vault's liquid balance in CSPR. " +
        "Returns the degraded diagnosis too (the endpoint answers 503 with details when the " +
        "chain is unreachable).")]
    public async Task<string> GetHealth()
    {
        // /health deliberately answers 503-with-JSON when the chain is down — that body IS the
        // diagnosis, so return it whenever it parses instead of collapsing it to an error line.
        var (ok, body) = await Get("/health", returnBodyOnErrorStatus: true);
        return body;
    }

    [McpServerTool(Name = "get_pending_proposals"), Description(
        "The co-sign queue: material (over-cap) proposals the agent has escalated that are " +
        "still awaiting the human owner's in-wallet co-sign or rejection. Returns only " +
        "unresolved proposals — id, validator, amount in CSPR, and whether it is an " +
        "undelegate (exit) or delegate move. Empty list = nothing awaiting the owner.")]
    public async Task<string> GetPendingProposals()
    {
        var (ok, body) = await Get("/api/state");
        if (!ok) return body;
        try
        {
            var root = JsonNode.Parse(body);
            var pending = new JsonArray();
            if (root?["state"]?["proposals"] is JsonArray proposals)
                foreach (var p in proposals)
                    if (p?["resolved"]?.GetValue<bool>() is false)
                        pending.Add(p.DeepClone());
            var result = new JsonObject
            {
                ["pendingProposals"] = pending,
                ["count"] = pending.Count,
                ["note"] = pending.Count == 0
                    ? "no material proposals are awaiting the owner's co-sign"
                    : "these over-cap moves execute only if the owner co-signs them in their own wallet (or resolves them with reject)",
            };
            return result.ToJsonString();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    // ────────────────────────────── static knowledge ──────────────────────────────

    [McpServerTool(Name = "explain_leash"), Description(
        "Explains the CHAINLEASH leash: the on-chain guardrails (GovernedVault contract on " +
        "Casper 2.0) that let an autonomous AI agent stake a treasury's CSPR but make it " +
        "cryptographically unable to steal, over-concentrate, or go rogue. Includes the roles, " +
        "the guard chain every agent move passes through, the custody invariant, and the " +
        "contract error-code table. Static reference — no network call.")]
    public static string ExplainLeash() => """
        CHAINLEASH — the leash, condensed
        =================================

        The vault (an Odra contract on Casper 2.0) HOLDS the treasury's CSPR and delegates from
        its OWN purse. An autonomous AI agent may rebalance the stake, but every limit below is
        enforced BY THE CHAIN ITSELF — not advisory, not server-side.

        ROLES
        - installer: recorded at install; the only address allowed to call `initialize` (closes
          the front-run window on a fresh deploy).
        - agent: the autonomous key. May delegate / undelegate / redelegate within the leash and
          propose_material. It can also deposit_bond and tighten_cap (only LOWER its own cap,
          never raise it). It cannot withdraw and cannot change policy.
        - owner: the human / institution (any account, incl. an M-of-N multisig). Co-signs
          material moves (approve_material), rejects proposals, sets all policy, engages the
          kill-switch, slashes/returns the bond, withdraws, recalls stake (owner_undelegate /
          owner_redelegate, allowed even while paused), and can rotate the agent or transfer
          ownership. agent == owner is rejected on-chain — the roles can never collapse.

        THE GUARD CHAIN (every agent move passes ALL of these or reverts on-chain)
        1. Per-action cap — any single move > value_cap reverts OverCap; over-cap moves must go
           through propose_material and wait for the owner's approve_material co-sign.
        2. Validator allowlist — delegating to a validator the owner has not allowlisted reverts
           ValidatorNotAllowed.
        3. Per-validator cap — a chain-side `committed` accumulator tracks stake directed at each
           validator, so over-concentration reverts PerValidatorCapExceeded and cannot be raced.
        4. Action cooldown — anti-thrash rate limit between agent moves; reverts RateLimited (also
           applies to propose_material, so a hijacked agent cannot spam the co-sign queue).
        5. Kill-switch — while the owner has set_paused(true), EVERY agent move reverts Paused.
        6. Slashable bond — the agent posts a bond the owner can forfeit (slash_bond) on a
           violation; withdraw always reserves the bond.

        THE CUSTODY INVARIANT
        No agent-reachable code path moves CSPR out of the vault. Only the owner can `withdraw`,
        and only to the owner. The agent can shift stake between allowlisted validators within
        its caps — it can never extract value. Off-chain services (dashboard, API, this MCP
        server) are read-only windows plus UNSIGNED transaction builders; the owner's wallet is
        the only place a state-changing owner transaction can be signed.

        CONTRACT ERROR CODES (surface on-chain as `User error: <code>`)
         1 NotInitialized           2 NotAgent                 3 NotOwner
         4 OverCap                  5 ValidatorNotAllowed      6 NoSuchProposal
         7 ProposalAlreadyResolved  8 AlreadyInitialized       9 CapNotLower
        10 InsufficientFreeBalance 11 Paused                  12 PerValidatorCapExceeded
        13 RateLimited             14 NotInstaller            15 ExceedsCommitted
        16 CapNotHigher            17 UnauthorizedBondDeposit 18 AgentOwnerSame
        (Odra reserves 64536+ for framework errors, e.g. 64658 = MissingArg.)
        """;

    // ────────────────────────────── owner-action preparation ──────────────────────────────

    [McpServerTool(Name = "prepare_owner_action"), Description(
        "Builds the UNSIGNED TransactionV1 JSON for an owner-gated vault action (kill-switch, " +
        "withdraw, recall, policy change...). The server returns an unsigned transaction; only " +
        "the owner's wallet can sign it. An AI agent can PREPARE but can never SIGN — that is " +
        "the leash. Actions: pause | unpause | withdraw | undelegate | redelegate | reject | " +
        "clearcommitted | raisecap | setmaxval | setcooldown | setvalidator | setcommission. " +
        "Required params per action — pause/unpause: none; withdraw/raisecap: amountCspr; " +
        "setmaxval: amountCspr (0 = unlimited); undelegate: validator + amountCspr; redelegate: " +
        "validator + newValidator + amountCspr; reject: id; clearcommitted: validator; " +
        "setcooldown: intervalSeconds (0 = disabled); setvalidator: validator + allowed; " +
        "setcommission: percent (0-100).")]
    public async Task<string> PrepareOwnerAction(
        [Description("The owner action: pause, unpause, withdraw, undelegate, redelegate, reject, clearcommitted, raisecap, setmaxval, setcooldown, setvalidator, setcommission")] string action,
        [Description("CSPR amount (for withdraw, undelegate, redelegate, raisecap, setmaxval)")] decimal? amountCspr = null,
        [Description("Validator public key hex (for undelegate, redelegate, clearcommitted, setvalidator)")] string? validator = null,
        [Description("Target validator public key hex (for redelegate)")] string? newValidator = null,
        [Description("Material proposal id (for reject)")] int? id = null,
        [Description("Cooldown in seconds, 0 = disabled (for setcooldown)")] int? intervalSeconds = null,
        [Description("true = add to allowlist, false = remove (for setvalidator)")] bool? allowed = null,
        [Description("Max commission percent 0-100 (for setcommission)")] int? percent = null)
    {
        var payload = new JsonObject { ["action"] = action };
        if (amountCspr is { } a) payload["amountCspr"] = a;
        if (!string.IsNullOrWhiteSpace(validator)) payload["validator"] = validator;
        if (!string.IsNullOrWhiteSpace(newValidator)) payload["newValidator"] = newValidator;
        if (id is { } i) payload["id"] = i;
        if (intervalSeconds is { } s) payload["intervalSeconds"] = s;
        if (allowed is { } al) payload["allowed"] = al;
        if (percent is { } p) payload["percent"] = p;

        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("/api/owner/prepare", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
                return
                    "UNSIGNED owner transaction built. Hand the transactionJson to the OWNER to sign " +
                    "in their own wallet (the dashboard's owner controls do exactly this) — no AI " +
                    "agent and no server can sign it.\n" + body;
            // 400 = bad params or no owner key configured; 500 = build failure — surface the
            // API's own {"error": ...} text, which is the useful part.
            var error = TryExtractError(body);
            return $"chainleash API rejected prepare_owner_action ({(int)resp.StatusCode}): {error}";
        }
        catch (Exception ex)
        {
            return Unreachable(ex);
        }
    }

    // ────────────────────────────── shared plumbing ──────────────────────────────

    /// <summary>GET a path; (true, body) on success, (false, one-line error) otherwise.</summary>
    private async Task<(bool Ok, string Body)> Get(string path, bool returnBodyOnErrorStatus = false)
    {
        try
        {
            using var resp = await http.GetAsync(path);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return (true, body);
            if (returnBodyOnErrorStatus && LooksLikeJson(body)) return (true, body);
            return (false, $"chainleash API unreachable: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, Unreachable(ex));
        }
    }

    private string Unreachable(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException =>
            $"chainleash API unreachable: timeout after {http.Timeout.TotalSeconds:0}s ({http.BaseAddress})",
        HttpRequestException hre =>
            $"chainleash API unreachable: {hre.Message} ({http.BaseAddress})",
        _ => $"chainleash API error: {ex.Message}",
    };

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    private static string TryExtractError(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["error"]?.GetValue<string>() is { Length: > 0 } msg) return msg;
        }
        catch (JsonException) { /* not JSON — fall through to the raw body */ }
        return string.IsNullOrWhiteSpace(body) ? "(no detail)" : body.Trim();
    }
}
