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
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(); // serves the built Angular dashboard from wwwroot in production

// Liveness + chain reachability + gas monitoring (for uptime checks / ops dashboards).
app.MapGet("/health", async (ChainReader chain, CasperVault vault, AuditFeed feed, IConfiguration cfg) =>
{
    var warn = cfg.GetValue("Agent:LowGasWarnCspr", 50m);
    decimal? vaultBal = null, gas = null;
    var chainOk = false; string? error = null;
    try
    {
        vaultBal = await chain.TotalBalanceCspr();           // proves chain reachable + vault resolved
        gas = await chain.AccountBalanceCspr(vault.AgentKey.ToAccountHex());
        chainOk = true;
    }
    catch (Exception ex) { error = ex.Message; }
    var lowGas = gas is < 0 ? false : gas is not null && gas < warn;
    var ok = chainOk && !feed.State.Paused && !lowGas;
    return Results.Json(new
    {
        status = ok ? "ok" : "degraded",
        chainReachable = chainOk,
        vaultResolved = vaultBal is not null,
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
}));

// Step 1 of the wallet co-sign: server builds the UNSIGNED approve_material tx for the
// owner to sign in their wallet. The server never holds the owner key.
app.MapGet("/api/approve/{id:int}/prepare", (int id, CasperVault vault) =>
{
    if (vault.OwnerKey is null) return Results.Json(new { error = "no owner public key configured" }, statusCode: 400);
    try
    {
        var txJson = vault.BuildUnsignedApproveMaterialJson((uint)id);
        return Results.Content("{\"transactionJson\":" + txJson + ",\"ownerPublicKey\":\"" + vault.OwnerKey.ToAccountHex() + "\"}", "application/json");
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// Step 2 of the wallet co-sign: the wallet submitted the signed tx; the browser reports the
// hash and we verify on-chain. approve_material is owner-gated, so success == the owner signed.
app.MapPost("/api/approve/{id:int}/confirm", async (int id, ConfirmReq body, CasperVault vault, AuditFeed feed) =>
{
    var p = feed.State.Proposals.FirstOrDefault(x => x.Id == (uint)id);
    var r = await vault.ConfirmCoSign(body.TxHash);
    if (r.Success)
        feed.State.Proposals = feed.State.Proposals.Select(x => x.Id == (uint)id ? x with { Resolved = true } : x).ToList();
    await feed.Push(new AuditEvent(
        DateTime.UtcNow.ToString("HH:mm:ss"), 0,
        r.Success ? "DELEGATE" : "REJECT",
        r.Success ? $"Owner co-signed material proposal #{id} in-wallet → executed on-chain." : $"Co-sign of #{id} not confirmed: {r.Error}",
        p?.Validator, p?.AmountCspr, r.Hash, r.Success));
    await feed.PushState();
    return Results.Json(new { r.Success, r.Hash, r.Error });
});

// Legacy server-key co-sign — DEV FALLBACK ONLY. Off by default; when on, optionally
// gated by a bearer token. The real path is the owner's own wallet (prepare + confirm).
app.MapPost("/api/approve/{id:int}", async (int id, CasperVault vault, AuditFeed feed, IConfiguration cfg, HttpContext http) =>
{
    if (!vault.AllowServerKeyCoSign)
        return Results.Json(new { Success = false, Error = "server-key co-sign disabled — use the in-wallet flow (GET .../prepare then POST .../confirm)" }, statusCode: 403);
    var token = cfg["Casper:CoSignBearerToken"];
    if (!string.IsNullOrWhiteSpace(token) &&
        http.Request.Headers.Authorization.ToString() != $"Bearer {token}")
        return Results.Json(new { Success = false, Error = "unauthorized" }, statusCode: 401);

    var p = feed.State.Proposals.FirstOrDefault(x => x.Id == (uint)id);
    var r = await vault.ApproveMaterial((uint)id);
    if (r.Success)
        feed.State.Proposals = feed.State.Proposals.Select(x => x.Id == (uint)id ? x with { Resolved = true } : x).ToList();
    await feed.Push(new AuditEvent(
        DateTime.UtcNow.ToString("HH:mm:ss"), 0,
        r.Success ? "DELEGATE" : "REJECT",
        r.Success ? $"Owner co-signed material proposal #{id} (server key) → executed on-chain." : $"Co-sign of #{id} failed: {r.Error}",
        p?.Validator, p?.AmountCspr, r.Hash, r.Success));
    await feed.PushState();
    return Results.Json(new { r.Success, r.Hash, r.Error });
});

app.MapHub<AuditHub>("/hub/audit");

app.Run();

// Body for POST /api/approve/{id}/confirm — the hash of the wallet-submitted co-sign tx.
record ConfirmReq(string TxHash);
