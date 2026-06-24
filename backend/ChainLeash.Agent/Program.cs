using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Casper.Network.SDK.Types;
using ChainLeash.Agent;

var builder = WebApplication.CreateBuilder(args);

// appsettings.json (committed defaults) + appsettings.local.json (gitignored: CSPR.cloud key + run config).
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton<CasperVault>();
builder.Services.AddSingleton<X402Client>();
builder.Services.AddSingleton<ValidatorMonitor>();
builder.Services.AddSingleton<ChainReader>();
builder.Services.AddSingleton<StakingService>();
builder.Services.AddSingleton<AllowlistStore>();
builder.Services.AddSingleton<AuditFeed>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddSignalR();
// CORS: in prod the dashboard is served same-origin from wwwroot (no CORS needed); allow ONLY
// the dev dashboard origin for `ng serve`. No AllowCredentials — the API uses no cookies/creds,
// and reflect-any-origin + credentials is the dangerous combination the Fetch spec forbids.
var devOrigin = builder.Configuration["Dashboard:DevCorsOrigin"] ?? "http://localhost:4200";
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(devOrigin).AllowAnyHeader().AllowAnyMethod()));
// Rate limiting blunts abuse of the public endpoints. Two lanes: a general "api" lane,
// and a much tighter "cosign" lane for the endpoints that fan out into upstream chain
// RPCs (a single /confirm can poll the chain for up to 2 minutes — the expensive lane
// must not inherit the cheap lane's budget).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 60, QueueLimit = 0 }));
    o.AddPolicy("cosign", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 10, QueueLimit = 0 }));
});

var app = builder.Build();

AuditEvent.ExplorerBase = (app.Configuration["Casper:ExplorerBaseUrl"] ?? "https://testnet.cspr.live").TrimEnd('/');

app.UseCors();
app.UseRateLimiter();

// Security headers on everything we serve (the API and the built dashboard alike).
// CSP notes: Angular AOT emits no inline <script> (so script-src stays strict) but does
// inject <style> tags ('unsafe-inline' for styles only); the CSPR.click wallet SDK loads
// from its CDN and talks to its own backends; SignalR needs ws(s) connect.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    h["Content-Security-Policy"] =
        // cloudflareinsights = Cloudflare Web Analytics beacon (injected at the edge); its
        // POST is already covered by connect-src https:.
        "default-src 'self'; script-src 'self' https://cdn.cspr.click https://static.cloudflareinsights.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " + // CSPR.click's wallet UI pulls Google Fonts
        "img-src 'self' data: https:; font-src 'self' data: https://fonts.gstatic.com; " +
        "connect-src 'self' ws: wss: https:; " +
        "frame-src https:; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(); // serves the built Angular dashboard from wwwroot in production

// Loud guard: never run the dev server-key co-sign fallback without a bearer token (it would
// expose an owner-key-signed approve_material to anyone who can reach the endpoint).
if (app.Configuration.GetValue("Casper:AllowServerKeyCoSign", false)
    && string.IsNullOrWhiteSpace(app.Configuration["Casper:CoSignBearerToken"]))
    app.Logger.LogWarning("⚠ Casper:AllowServerKeyCoSign is ON but Casper:CoSignBearerToken is empty — the server-key co-sign endpoint will FAIL CLOSED. Set a bearer token (dev only) or disable the fallback.");

// A given co-sign tx may resolve at most ONE proposal, once — defeats replay of a genuine
// (public) co-sign tx hash to re-forge audit entries. Bounded so garbage hashes can't grow
// it forever; cross-restart replay is covered by the proposal queue being CHAIN truth (a
// replayed historical co-sign targets a proposal the chain already reports as resolved).
var consumedCoSign = new BoundedSet(4096);

// Process-liveness probe for the CONTAINER healthcheck — ZERO chain reads, so a 30s Docker probe
// can't hammer the node-RPC quota (the old healthcheck hit /health → 2 query_balance every 30s ≈
// 5,760 node-RPC calls/day, the single biggest quota drain). A real chain outage is surfaced by the
// agent tick (it HOLDs + flags Stale) and by /health below (the rich on-demand ops endpoint) — not
// by restarting the container, which can't fix an upstream outage and would only crash-loop.
app.MapGet("/healthz", () => Results.Text("ok"));

// Rich chain reachability + gas monitoring for OPERATORS/uptime monitors (on-demand, not the 30s
// container probe). Does a live chain read each call, so don't point a high-frequency probe at it.
app.MapGet("/health", async (ChainReader chain, CasperVault vault, X402Client x402, AuditFeed feed, IConfiguration cfg, ILoggerFactory lf) =>
{
    var warn = cfg.GetValue("Agent:LowGasWarnCspr", 50m);
    decimal? vaultBal = null, gas = null;
    var chainOk = false; string? error = null;
    try
    {
        vaultBal = await chain.TotalBalanceCspr();           // proves chain reachable + vault resolved
        if (vault.AgentKey is not null)                      // observer mode has no agent key / gas wallet
            gas = await chain.AccountBalanceCspr(vault.AgentKey.ToAccountHex());
        chainOk = true;
    }
    catch (Exception ex)
    {
        error = "chain unreachable";                          // detail stays server-side
        lf.CreateLogger("Health").LogWarning(ex, "health probe failed");
    }
    var lowGas = gas is not null && gas < warn;
    var ok = chainOk && !feed.State.Paused && !lowGas;
    return Results.Json(new
    {
        status = ok ? "ok" : "degraded",
        chainReachable = chainOk,
        vaultResolved = vaultBal is not null,
        readOnly = vault.ReadOnly,                            // observer mode (no agent key)
        x402Enabled = !x402.Disabled,                         // a silently-degraded pay-to-think should be visible
        stale = feed.State.Stale,                             // last chain read failed → values may be stale
        paused = feed.State.Paused,
        agentGasCspr = gas,
        lowGas,
        vaultBalanceCspr = vaultBal,
        error,
    }, statusCode: chainOk ? 200 : 503);
}).RequireRateLimiting("api");

// Snapshot of the live leash + agent state plus recent audit history (for first paint).
app.MapGet("/api/state", (AuditFeed feed) => Results.Json(new { state = feed.State, events = feed.Recent() }))
    .RequireRateLimiting("api");

// Where the vault delegated + how much it earned. The vault is a contract (uref) delegator, so
// cspr.live can't show this; CSPR.cloud's delegation index can. Read-only, cached ~per era.
app.MapGet("/api/staking", async (StakingService staking, HttpContext http) =>
    Results.Json(await staking.GetAsync(http.RequestAborted)))
    .RequireRateLimiting("api");

// Public config the dashboard needs to drive the in-browser wallet co-sign (no secrets).
app.MapGet("/api/config", (CasperVault vault, X402Client x402, IConfiguration cfg) => Results.Json(new
{
    chainName = cfg["Casper:ChainName"],
    packageHash = cfg["Casper:GovernedVaultPackageHash"],
    explorerBaseUrl = AuditEvent.ExplorerBase,                // single source of truth for explorer links
    ownerPublicKey = vault.OwnerKey?.ToAccountHex(),
    csprClickAppId = cfg["Dashboard:CsprClickAppId"],
    walletCoSignEnabled = vault.OwnerKey is not null,
    allowServerKeyCoSign = vault.AllowServerKeyCoSign,
    readOnly = vault.ReadOnly,                                // observer mode: agent signs nothing
    x402Enabled = !x402.Disabled,                             // pay-to-think wired (key + provider pubkey present)
})).RequireRateLimiting("api");

// Step 1 of the wallet co-sign: server builds the UNSIGNED approve_material tx for the
// owner to sign in their wallet. The server never holds the owner key.
app.MapGet("/api/approve/{id:int}/prepare", (int id, CasperVault vault, ILoggerFactory lf) =>
{
    if (id < 0) return Results.Json(new { error = "invalid id" }, statusCode: 400); // {id:int} admits negatives, which (uint) would wrap
    if (vault.OwnerKey is null) return Results.Json(new { error = "no owner public key configured" }, statusCode: 400);
    try
    {
        var txJson = vault.BuildUnsignedApproveMaterialJson((uint)id);
        return Results.Content("{\"transactionJson\":" + txJson + ",\"ownerPublicKey\":\"" + vault.OwnerKey.ToAccountHex() + "\"}", "application/json");
    }
    catch (Exception ex)
    {
        lf.CreateLogger("CoSign").LogWarning(ex, "prepare #{Id} failed", id); // detail stays server-side
        return Results.Json(new { error = "could not build the co-sign transaction" }, statusCode: 500);
    }
}).RequireRateLimiting("cosign");

// Step 2 of the wallet co-sign: the wallet submitted the signed tx; the browser reports the
// hash and we verify on-chain. approve_material is owner-gated, so success == the owner signed.
app.MapPost("/api/approve/{id:int}/confirm", async (int id, ConfirmReq body, CasperVault vault, AuditFeed feed, ILoggerFactory lf, HttpContext http) =>
{
    var log = lf.CreateLogger("CoSign");
    if (id < 0) return Results.Json(new { Success = false, Error = "invalid id" }, statusCode: 400);
    var hash = (body?.TxHash ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(hash) || hash.Length is < 32 or > 80 || !hash.All(Uri.IsHexDigit))
        return Results.Json(new { Success = false, Error = "invalid txHash" }, statusCode: 400);

    var p = feed.State.Proposals.FirstOrDefault(x => x.Id == (uint)id);
    if (p is { Resolved: true }) // already co-signed — don't re-poll the chain or re-emit
        return Results.Json(new { Success = false, Error = "proposal already resolved" }, statusCode: 409);
    if (!consumedCoSign.TryAdd(hash)) // single-use: this co-sign tx can't be replayed
        return Results.Json(new { Success = false, Error = "co-sign already processed" }, statusCode: 409);

    // ConfirmCoSign verifies on-chain that this tx really is approve_material(id) on THIS vault,
    // owner-initiated and successful — so success == a genuine owner co-sign, not a forged hash.
    // The browser's disconnect aborts the (up to 2-minute) chain poll via RequestAborted.
    var r = await vault.ConfirmCoSign((uint)id, hash, http.RequestAborted);
    if (!r.Success)
    {
        // keep forged/failed hashes consumed (anti-spam); only re-allow a retry for a tx
        // that simply wasn't visible on-chain yet (timeout / client disconnect mid-poll)
        var err = r.Error ?? "";
        if (err.Contains("timeout", StringComparison.OrdinalIgnoreCase) || err.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            consumedCoSign.Remove(hash);
        log.LogInformation("co-sign #{Id} not confirmed: {Err}", id, r.Error); // detail stays server-side
        await feed.Push(new AuditEvent(DateTime.UtcNow.ToString("HH:mm:ss"), 0, "REJECT",
            $"Co-sign of #{id} was not confirmed on-chain.", p?.Validator, p?.AmountCspr, null, false)); // no attacker-controlled text/hash
        await feed.PushState();
        return Results.Json(new { Success = false, Error = "not confirmed on-chain" }, statusCode: 400);
    }
    feed.State.Proposals = feed.State.Proposals.Select(x => x.Id == (uint)id ? x with { Resolved = true } : x).ToList();
    await feed.Push(new AuditEvent(DateTime.UtcNow.ToString("HH:mm:ss"), 0, "DELEGATE",
        $"Owner co-signed material proposal #{id} in-wallet → executed on-chain.", p?.Validator, p?.AmountCspr, r.Hash, true));
    await feed.PushState();
    return Results.Json(new { r.Success, r.Hash });
}).RequireRateLimiting("cosign");

// Legacy server-key co-sign — DEV FALLBACK ONLY. Off by default; when on, optionally
// gated by a bearer token. The real path is the owner's own wallet (prepare + confirm).
app.MapPost("/api/approve/{id:int}", async (int id, CasperVault vault, AuditFeed feed, IConfiguration cfg, HttpContext http) =>
{
    if (id < 0) return Results.Json(new { Success = false, Error = "invalid id" }, statusCode: 400);
    if (!vault.AllowServerKeyCoSign)
        return Results.Json(new { Success = false, Error = "server-key co-sign disabled — use the in-wallet flow (GET .../prepare then POST .../confirm)" }, statusCode: 403);
    var token = cfg["Casper:CoSignBearerToken"];
    if (string.IsNullOrWhiteSpace(token)) // FAIL CLOSED: never sign with the owner key without a token
        return Results.Json(new { Success = false, Error = "server-key co-sign requires Casper:CoSignBearerToken to be set" }, statusCode: 403);
    var provided = Encoding.UTF8.GetBytes(http.Request.Headers.Authorization.ToString());
    var expected = Encoding.UTF8.GetBytes($"Bearer {token}");
    if (provided.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(provided, expected))
        return Results.Json(new { Success = false, Error = "unauthorized" }, statusCode: 401);

    var p = feed.State.Proposals.FirstOrDefault(x => x.Id == (uint)id);
    var r = await vault.ApproveMaterial((uint)id);
    if (r.Success)
        feed.State.Proposals = feed.State.Proposals.Select(x => x.Id == (uint)id ? x with { Resolved = true } : x).ToList();
    await feed.Push(new AuditEvent(
        DateTime.UtcNow.ToString("HH:mm:ss"), 0,
        r.Success ? "DELEGATE" : "REJECT",
        r.Success ? $"Owner co-signed material proposal #{id} (server key) → executed on-chain." : $"Server-key co-sign of #{id} failed.",
        p?.Validator, p?.AmountCspr, r.Hash, r.Success));
    await feed.PushState();
    return Results.Json(new { r.Success, r.Hash, r.Error });
}).RequireRateLimiting("cosign");

// ───────────────────────── OWNER DIRECT CONTROLS ─────────────────────────
// Stop the agent (kill-switch), recall staked CSPR to the vault, withdraw free balance to the
// owner's wallet, redelegate, or reject a proposal — all in the owner's OWN wallet, same flow as
// the material co-sign: server builds the UNSIGNED owner tx, the owner signs it in their wallet,
// the server confirms on-chain. Every entry point is owner-gated in the contract, so a confirmed
// success proves the owner signed; the server never holds the owner key.

// Step 1 — build the unsigned owner-action tx for the wallet to sign.
app.MapPost("/api/owner/prepare", (OwnerPrepareReq body, CasperVault vault, ILoggerFactory lf) =>
{
    if (vault.OwnerKey is null) return Results.Json(new { error = "no owner public key configured" }, statusCode: 400);
    var action = (body?.Action ?? "").Trim().ToLowerInvariant();
    try
    {
        string json = action switch
        {
            "pause"      => vault.PrepareSetPaused(true),
            "unpause"    => vault.PrepareSetPaused(false),
            "withdraw"   => vault.PrepareWithdraw(OwnerActions.ToMotes(body!.AmountCspr)),
            "undelegate" => vault.PrepareOwnerUndelegate(PublicKey.FromHexString(OwnerActions.RequireHex(body!.Validator)), OwnerActions.ToMotes(body.AmountCspr)),
            "redelegate" => vault.PrepareOwnerRedelegate(PublicKey.FromHexString(OwnerActions.RequireHex(body!.Validator)), PublicKey.FromHexString(OwnerActions.RequireHex(body.NewValidator)), OwnerActions.ToMotes(body.AmountCspr)),
            "reject"     => vault.PrepareRejectMaterial((uint)OwnerActions.RequireId(body!.Id)),
            "raisecap"   => vault.PrepareRaiseCap(OwnerActions.ToMotes(body!.AmountCspr)),
            "setmaxval"  => vault.PrepareSetMaxPerValidator(OwnerActions.ToMotesAllowZero(body!.AmountCspr)),
            "setcooldown"=> vault.PrepareSetActionInterval(OwnerActions.ToMs(body!.IntervalSeconds)),
            "setvalidator" => vault.PrepareSetValidator(PublicKey.FromHexString(OwnerActions.RequireHex(body!.Validator)), body.Allowed ?? false),
            _            => throw new ArgumentException("unknown action"),
        };
        return Results.Content("{\"transactionJson\":" + json + ",\"ownerPublicKey\":\"" + vault.OwnerKey.ToAccountHex() + "\"}", "application/json");
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 400); // bad amount / validator hex / id
    }
    catch (Exception ex)
    {
        lf.CreateLogger("Owner").LogWarning(ex, "owner prepare ({Action}) failed", action); // detail stays server-side
        return Results.Json(new { error = "could not build the owner transaction" }, statusCode: 500);
    }
}).RequireRateLimiting("cosign");

// Step 2 — the wallet submitted the signed owner tx; verify it on-chain, then reflect it.
app.MapPost("/api/owner/confirm", async (OwnerConfirmReq body, CasperVault vault, AuditFeed feed, AllowlistStore allowlist, ILoggerFactory lf, HttpContext http) =>
{
    var log = lf.CreateLogger("Owner");
    var action = (body?.Action ?? "").Trim().ToLowerInvariant();
    var entryPoint = OwnerActions.EntryPoint(action);
    if (entryPoint is null) return Results.Json(new { Success = false, Error = "unknown action" }, statusCode: 400);
    var hash = (body!.TxHash ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(hash) || hash.Length is < 32 or > 80 || !hash.All(Uri.IsHexDigit))
        return Results.Json(new { Success = false, Error = "invalid txHash" }, statusCode: 400);
    if (!consumedCoSign.TryAdd(hash)) // single-use: an owner tx can't be replayed to re-forge an audit entry
        return Results.Json(new { Success = false, Error = "action already processed" }, statusCode: 409);

    // ConfirmOwnerTx verifies on-chain that this tx really is `entryPoint` on THIS vault,
    // owner-initiated and successful — so success == a genuine owner action, not a forged hash.
    var r = await vault.ConfirmOwnerTx(entryPoint, hash, http.RequestAborted);
    if (!r.Success)
    {
        var err = r.Error ?? "";
        var transient = err.Contains("timeout", StringComparison.OrdinalIgnoreCase) || err.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
        if (transient) consumedCoSign.Remove(hash); // simply not visible yet — allow a retry
        log.LogInformation("owner action {Action} not confirmed: {Err}", action, r.Error);
        // The on-chain revert reason (a contract/auction error) is safe to show — the action is
        // owner-gated, so it isn't attacker-controlled — and far more useful than a bare 400. Return
        // 200 so the dashboard renders the reason (a 400 surfaces only as a generic "Http failure").
        var shown = transient ? "Not confirmed on-chain yet — please retry in a moment."
                              : string.IsNullOrWhiteSpace(err) ? "The action did not succeed on-chain." : err;
        return Results.Json(new { Success = false, Error = shown });
    }

    // Optimistic, chain-truthful update so the dashboard reflects the action immediately — the
    // worker's next tick rebuilds the whole state from chain regardless, but its cadence is now
    // per-era, and a kill-switch must show as engaged at once. (The tx already executed on-chain.)
    // An owner-confirmed set_validator(true) means a (possibly brand-new) validator is now on the
    // on-chain allowlist — add it to the agent's watch-list so it actually perceives + can use it.
    if (action == "setvalidator" && body.Allowed == true && !string.IsNullOrEmpty(body.Validator))
        allowlist.Add(body.Validator);
    var msg = OwnerActions.ApplyEffect(feed.State, action, body);
    await feed.Push(new AuditEvent(DateTime.UtcNow.ToString("HH:mm:ss"), 0, "OWNER", msg,
        body.Validator, body.AmountCspr, r.Hash, true, DateTime.UtcNow.ToString("o")));
    await feed.PushState();
    return Results.Json(new { r.Success, r.Hash });
}).RequireRateLimiting("cosign");

app.MapHub<AuditHub>("/hub/audit");

app.Run();

// Body for POST /api/approve/{id}/confirm — the hash of the wallet-submitted co-sign tx.
record ConfirmReq(string TxHash);

// Bodies for the owner-direct controls. `Action` selects the owner-gated entry point; the rest
// are the action's params (CSPR amount, validator(s), proposal id) — only those it needs are set.
record OwnerPrepareReq(string Action, decimal? AmountCspr, string? Validator, string? NewValidator, int? Id, int? IntervalSeconds, bool? Allowed);
record OwnerConfirmReq(string Action, string TxHash, decimal? AmountCspr, string? Validator, string? NewValidator, int? Id, int? IntervalSeconds, bool? Allowed);

/// Server-side whitelist + effects for the owner-direct controls. Keeping the action→entry-point
/// map here (not client-supplied) means /confirm always verifies the entry point the action
/// claims, so a pause tx hash can't be confirmed as an undelegate.
static class OwnerActions
{
    public static string? EntryPoint(string action) => action switch
    {
        "pause" or "unpause" => "set_paused",
        "withdraw"           => "withdraw",
        "undelegate"         => "owner_undelegate",
        "redelegate"         => "owner_redelegate",
        "reject"             => "reject_material",
        "raisecap"           => "raise_cap",
        "setmaxval"          => "set_max_per_validator",
        "setcooldown"        => "set_action_interval",
        "setvalidator"       => "set_validator",
        _                    => null,
    };

    public static ulong ToMotes(decimal? cspr)
    {
        if (cspr is not { } v || v <= 0) throw new ArgumentException("amount must be positive");
        var motes = decimal.Truncate(v * 1_000_000_000m);
        if (motes > ulong.MaxValue) throw new ArgumentException("amount too large");
        return (ulong)motes;
    }

    /// Like ToMotes but allows 0 (e.g. set the per-validator cap to 0 = unlimited).
    public static ulong ToMotesAllowZero(decimal? cspr)
    {
        if (cspr is not { } v || v < 0) throw new ArgumentException("amount must be ≥ 0");
        var motes = decimal.Truncate(v * 1_000_000_000m);
        if (motes > ulong.MaxValue) throw new ArgumentException("amount too large");
        return (ulong)motes;
    }

    /// Cooldown seconds → milliseconds (0 = disabled).
    public static ulong ToMs(int? seconds)
    {
        if (seconds is not { } s || s < 0) throw new ArgumentException("cooldown must be ≥ 0 seconds");
        return (ulong)s * 1000;
    }

    public static int RequireId(int? id) => id is { } v && v >= 0 ? v : throw new ArgumentException("missing or invalid id");

    public static string RequireHex(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && hex.All(Uri.IsHexDigit) ? hex : throw new ArgumentException("missing or invalid validator key");

    /// Optimistically mirror an owner action that already executed on-chain into the live state,
    /// and return the audit-feed message. Amounts/validators come from the request and drive only
    /// the DISPLAY; the on-chain effect is whatever the owner signed, reconciled next tick.
    public static string ApplyEffect(FeedState s, string action, OwnerConfirmReq body)
    {
        var amt = body.AmountCspr ?? 0m;
        switch (action)
        {
            case "pause":
                s.Paused = true;
                return "Owner engaged the kill-switch — agent paused. Every agent move is rejected on-chain until resumed.";
            case "unpause":
                s.Paused = false;
                return "Owner released the kill-switch — agent resumed.";
            case "withdraw":
                s.TotalBalanceCspr = Math.Max(0, s.TotalBalanceCspr - amt);
                s.FreeBalanceCspr = Math.Max(0, s.FreeBalanceCspr - amt);
                return $"Owner withdrew {amt:N0} CSPR of free balance to their wallet.";
            case "undelegate":
                ReduceCommitted(s, body.Validator, amt);
                return $"Owner recalled {amt:N0} CSPR from {Short(body.Validator)} — unbonds to the vault over ~7 eras, then Withdraw moves it to your wallet.";
            case "redelegate":
                ReduceCommitted(s, body.Validator, amt);
                AddCommitted(s, body.NewValidator, amt);
                return $"Owner moved {amt:N0} CSPR from {Short(body.Validator)} → {Short(body.NewValidator)}.";
            case "reject":
                if (body.Id is { } id and >= 0)
                    s.Proposals = s.Proposals.Select(p => p.Id == (uint)id ? p with { Resolved = true } : p).ToList();
                return $"Owner rejected material proposal #{body.Id} — resolved without executing.";
            case "raisecap":
                s.CapCspr = amt;
                return $"Owner raised the per-action cap to {amt:N0} CSPR.";
            case "setmaxval":
                s.MaxPerValidatorCspr = amt;
                return amt > 0 ? $"Owner set the per-validator cap to {amt:N0} CSPR." : "Owner removed the per-validator cap (unlimited).";
            case "setcooldown":
                var secs = body.IntervalSeconds ?? 0;
                s.ActionIntervalMs = (ulong)(secs < 0 ? 0 : secs) * 1000;
                return secs > 0 ? $"Owner set the action cooldown to {secs}s." : "Owner disabled the action cooldown.";
            case "setvalidator":
                var allow = body.Allowed ?? false;
                if (!string.IsNullOrEmpty(body.Validator))
                    s.Validators = s.Validators.Select(v => string.Equals(v.PublicKey, body.Validator, StringComparison.OrdinalIgnoreCase)
                        ? v with { Allowed = allow } : v).ToList();
                return $"Owner {(allow ? "allowed" : "removed")} validator {Short(body.Validator)} {(allow ? "on" : "from")} the allowlist.";
            default:
                return "Owner action confirmed on-chain.";
        }
    }

    static void ReduceCommitted(FeedState s, string? validatorHex, decimal amt)
    {
        if (string.IsNullOrEmpty(validatorHex)) return;
        s.Validators = s.Validators.Select(v => string.Equals(v.PublicKey, validatorHex, StringComparison.OrdinalIgnoreCase)
            ? v with { DelegatedCspr = Math.Max(0, v.DelegatedCspr - amt) } : v).ToList();
    }

    static void AddCommitted(FeedState s, string? validatorHex, decimal amt)
    {
        if (string.IsNullOrEmpty(validatorHex)) return;
        s.Validators = s.Validators.Select(v => string.Equals(v.PublicKey, validatorHex, StringComparison.OrdinalIgnoreCase)
            ? v with { DelegatedCspr = v.DelegatedCspr + amt } : v).ToList();
    }

    static string Short(string? s) => s is { Length: > 10 } ? s[..10] + "…" : (s ?? "");
}
