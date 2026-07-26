using ChainLeash.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// CHAINLEASH MCP server — lets any MCP client (Claude Desktop, Claude Code, ...) supervise the
// live on-chain staking vault through the project's public read-only HTTPS API. The one write-ish
// tool (prepare_owner_action) only ever produces an UNSIGNED transaction: an AI agent can PREPARE
// an owner action but can never SIGN it — that is the leash.

var builder = Host.CreateApplicationBuilder(args);

// stdio transport: stdout carries the MCP protocol frames — every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// One shared HttpClient for all tools. Base URL is overridable for local/dev runs.
var apiBase = (Environment.GetEnvironmentVariable("CHAINLEASH_API") ?? "https://chainleash.ekolsoft.com").TrimEnd('/');
builder.Services.AddSingleton(new HttpClient
{
    BaseAddress = new Uri(apiBase),
    Timeout = TimeSpan.FromSeconds(10),
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ChainLeashTools>();

await builder.Build().RunAsync();
