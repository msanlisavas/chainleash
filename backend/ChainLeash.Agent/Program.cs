using ChainLeash.Agent;

var builder = WebApplication.CreateBuilder(args);

// appsettings.json (committed defaults) + appsettings.local.json (gitignored: CSPR.cloud key + run config).
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton<CasperVault>();
builder.Services.AddSingleton<X402Client>();
builder.Services.AddSingleton<ValidatorMonitor>();
builder.Services.AddSingleton<AuditFeed>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(); // serves the built Angular dashboard from wwwroot in production

// Snapshot of the live leash + agent state plus recent audit history (for first paint).
app.MapGet("/api/state", (AuditFeed feed) => Results.Json(new { state = feed.State, events = feed.Recent() }));

// Owner co-signs a pending material proposal → executes it on-chain (human-in-the-loop).
app.MapPost("/api/approve/{id:int}", async (int id, CasperVault vault, AuditFeed feed) =>
{
    var p = feed.State.Proposals.FirstOrDefault(x => x.Id == (uint)id);
    var r = await vault.ApproveMaterial((uint)id);
    if (r.Success)
        feed.State.Proposals = feed.State.Proposals.Select(x => x.Id == (uint)id ? x with { Resolved = true } : x).ToList();
    await feed.Push(new AuditEvent(
        DateTime.UtcNow.ToString("HH:mm:ss"), 0,
        r.Success ? "DELEGATE" : "REJECT",
        r.Success ? $"Owner co-signed material proposal #{id} → executed on-chain." : $"Co-sign of #{id} failed: {r.Error}",
        p?.Validator, p?.AmountCspr, r.Hash, r.Success));
    await feed.PushState();
    return Results.Json(new { r.Success, r.Hash, r.Error });
});

app.MapHub<AuditHub>("/hub/audit");

app.Run();
