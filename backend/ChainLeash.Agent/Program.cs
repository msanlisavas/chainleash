using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using ChainLeash.Agent;

var builder = WebApplication.CreateBuilder(args);

// appsettings.json (committed defaults) + appsettings.local.json (gitignored: CSPR.cloud key + run config).
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton<CasperVault>();
builder.Services.AddSingleton<X402Client>();
builder.Services.AddSingleton<ValidatorMonitor>();
builder.Services.AddSingleton<ChainReader>();
builder.Services.AddSingleton<AuditFeed>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddSignalR();
// CORS: in prod the dashboard is served same-origin from wwwroot (no CORS needed); allow ONLY
// the dev dashboard origin for `ng serve`. No AllowCredentials — the API uses no cookies/creds,
// and reflect-any-origin + credentials is the dangerous combination the Fetch spec forbids.
var devOrigin = builder.Configuration["Dashboard:DevCorsOrigin"] ?? "http://localhost:4200";
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(devOrigin).AllowAnyHeader().AllowAnyMethod()));
// Rate limiting blunts abuse of the public endpoints — especially the chain-polling co-sign path.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 60, QueueLimit = 0 }));
});

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles(); // serves the built Angular dashboard from wwwroot in production

// Loud guard: never run the dev server-key co-sign fallback without a bearer token (it would
// expose an owner-key-signed approve_material to anyone who can reach the endpoint).
if (app.Configuration.GetValue("Casper:AllowServerKeyCoSign", false)
    && string.IsNullOrWhiteSpace(app.Configuration["Casper:CoSignBearerToken"]))
    app.Logger.LogWarning("⚠ Casper:AllowServerKeyCoSign is ON but Casper:CoSignBearerToken is empty — the server-key co-sign endpoint will FAIL CLOSED. Set a bearer token (dev only) or disable the fallback.");

// A given co-sign tx may resolve at most ONE proposal, once — defeats replay of a genuine
// (public) co-sign tx hash to re-forge audit entries. In-memory: a restart can't un-resolve
// an already-resolved proposal anyway (the unresolved-check below covers cross-restart).
var consumedCoSign = new ConcurrentDictionary<string, byte>();

// Liveness + chain reachability + gas monitoring (for uptime checks / ops dashboards).
app.MapGet("/health", async (ChainReader chain, CasperVault vault, AuditFeed feed, IConfiguration cfg) =>
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
    catch (Exception ex) { error = ex.Message; }
    var lowGas = gas is not null && gas < warn;
    var ok = chainOk && !feed.State.Paused && !lowGas;
    return Results.Json(new
    {
        status = ok ? "ok" : "degraded",
        chainReachable = chainOk,
        vaultResolved = vaultBal is not null,
        readOnly = vault.ReadOnly,                            // observer mode (no agent key)
        stale = feed.State.Stale,                             // last chain read failed → values may be stale
        paused = feed.State.Paused,
        agentGasCspr = gas,
        lowGas,
        vaultBalanceCspr = vaultBal,
        error,
    }, statusCode: chainOk ? 200 : 503);
});

// Snapshot of the live leash + agent state plus recent audit history (for first paint).
app.MapGet("/api/state", (AuditFeed feed) => Results.Json(new { state = feed.State, events = feed.Recent() }));

// Public config the dashboard needs to drive the in-browser wallet co-sign (no secrets).
app.MapGet("/api/config", (CasperVault vault, IConfiguration cfg) => Results.Json(new
{
    chainName = cfg["Casper:ChainName"],
    packageHash = cfg["Casper:GovernedVaultPackageHash"],
    ownerPublicKey = vault.OwnerKey?.ToAccountHex(),
    csprClickAppId = cfg["Dashboard:CsprClickAppId"],
    walletCoSignEnabled = vault.OwnerKey is not null,
    allowServerKeyCoSign = vault.AllowServerKeyCoSign,
    readOnly = vault.ReadOnly,                                // observer mode: agent signs nothing
}));

// Step 1 of the wallet co-sign: server builds the UNSIGNED approve_material tx for the
// owner to sign in their wallet. The server never holds the owner key.
app.MapGet("/api/approve/{id:int}/prepare", (int id, CasperVault vault, ILoggerFactory lf) =>
{
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
}).RequireRateLimiting("api");

// Step 2 of the wallet co-sign: the wallet submitted the signed tx; the browser reports the
// hash and we verify on-chain. approve_material is owner-gated, so success == the owner signed.
app.MapPost("/api/approve/{id:int}/confirm", async (int id, ConfirmReq body, CasperVault vault, AuditFeed feed, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("CoSign");
    var hash = (body?.TxHash ?? "").Trim();
    if (string.IsNullOrWhiteSpace(hash) || hash.Length is < 32 or > 80 || !hash.All(Uri.IsHexDigit))
        return Results.Json(new { Success = false, Error = "invalid txHash" }, statusCode: 400);

    var p = feed.State.Proposals.FirstOrDefault(x => x.Id == (uint)id);
    if (p is { Resolved: true }) // already co-signed — don't re-poll the chain or re-emit
        return Results.Json(new { Success = false, Error = "proposal already resolved" }, statusCode: 409);
    if (!consumedCoSign.TryAdd(hash, 1)) // single-use: this co-sign tx can't be replayed
        return Results.Json(new { Success = false, Error = "co-sign already processed" }, statusCode: 409);

    // ConfirmCoSign verifies on-chain that this tx really is approve_material(id) on THIS vault,
    // owner-initiated and successful — so success == a genuine owner co-sign, not a forged hash.
    var r = await vault.ConfirmCoSign((uint)id, hash);
    if (!r.Success)
    {
        // keep forged/failed hashes consumed (anti-spam); only re-allow a not-yet-on-chain retry
        if ((r.Error ?? "").Contains("timeout", StringComparison.OrdinalIgnoreCase)) consumedCoSign.TryRemove(hash, out _);
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
}).RequireRateLimiting("api");

// Legacy server-key co-sign — DEV FALLBACK ONLY. Off by default; when on, optionally
// gated by a bearer token. The real path is the owner's own wallet (prepare + confirm).
app.MapPost("/api/approve/{id:int}", async (int id, CasperVault vault, AuditFeed feed, IConfiguration cfg, HttpContext http) =>
{
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
}).RequireRateLimiting("api");

app.MapHub<AuditHub>("/hub/audit");

app.Run();

// Body for POST /api/approve/{id}/confirm — the hash of the wallet-submitted co-sign tx.
record ConfirmReq(string TxHash);
