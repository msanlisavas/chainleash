using ChainLeash.Agent;

var builder = Host.CreateApplicationBuilder(args);

// settings.json (committed) + settings.local.json (gitignored: CSPR.cloud key) override.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton<CasperVault>();
builder.Services.AddSingleton<X402Client>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
