// CHAINLEASH — Week-1 de-risking spike harness.
// See docs/superpowers/plans/2026-06-05-chainleash-foundation-spike.md
//
// Usage:  dotnet run -- <command>
//   keygen             Generate agent + human ED25519 key pairs into secrets/
//   smoke              Verify node RPC + config (prints node API/build version)
//   balance            Show the agent account balance + thresholds (via CSPR.cloud REST)
//   setup-keys         [Spike A] configure weighted-key thresholds on the treasury account
//   attempt-overreach  [Spike B] agent-only key_management op — capture what the network returns
//   cosign-op          [Spike C] human/weighted co-sign success path
//   help               Show this help

using Casper.Network.SDK;
using Casper.Network.SDK.Types;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

IConfiguration Config() => new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("Config/settings.example.json", optional: true)  // defaults
    .AddJsonFile("Config/settings.local.json", optional: true)    // overrides (real key, gitignored)
    .Build();

// Build an HttpClient that carries the CSPR.cloud access key (for the node RPC + REST).
HttpClient AuthedHttp(IConfiguration cfg)
{
    var http = new HttpClient();
    var key = cfg["CsprCloudAccessKey"];
    if (!string.IsNullOrWhiteSpace(key) && !key.StartsWith("<"))
        http.DefaultRequestHeaders.Add("Authorization", key);
    return http;
}

switch (command)
{
    case "keygen": Keygen(); break;
    case "smoke": await Smoke(); break;
    case "balance": await Balance(); break;

    // Live spike experiments — implemented during the spike session (Plan 1, Tasks 3-5).
    // They construct weighted-key / contract deploys via Casper.Network.SDK and we record
    // exactly what the live testnet returns. Stubbed here so the harness builds & runs.
    case "setup-keys":
    case "attempt-overreach":
    case "cosign-op":
        Console.WriteLine($"[{command}] Spike experiment not yet implemented — see Plan 1.");
        Console.WriteLine("We implement this live against testnet and record the network response.");
        break;

    default: Help(); break;
}

// --- keygen: produce agent + human key pairs (no network needed) ---
void Keygen()
{
    foreach (var name in new[] { "agent", "human" })
    {
        var dir = Path.Combine("secrets", name);
        Directory.CreateDirectory(dir);
        var kp = KeyPair.CreateNew(KeyAlgo.ED25519);
        kp.WriteToPem(Path.Combine(dir, "secret_key.pem"));
        var hex = kp.PublicKey.ToAccountHex();
        File.WriteAllText(Path.Combine(dir, "public_key_hex"), hex);
        Console.WriteLine($"{name,-6} {hex}");
    }
    Console.WriteLine("Keys written under secrets/ (gitignored). Fund the agent at the testnet faucet.");
}

// --- smoke: prove node RPC reachability through CSPR.cloud (authed) ---
async Task Smoke()
{
    var cfg = Config();
    var nodeUrl = cfg["NodeRpcUrl"];
    if (string.IsNullOrWhiteSpace(nodeUrl))
    {
        Console.WriteLine("No NodeRpcUrl. Copy Config/settings.example.json to settings.local.json and fill it in.");
        return;
    }
    var client = new NetCasperClient(nodeUrl, AuthedHttp(cfg));
    var status = (await client.GetNodeStatus()).Parse();
    Console.WriteLine($"Node reachable: {nodeUrl}");
    Console.WriteLine($"API {status.ApiVersion} | build {status.BuildVersion} | chainspec {status.ChainspecName}");
    Console.WriteLine($"Config ChainName: {cfg["ChainName"]}");
}

// --- balance: agent account balance + current thresholds (CSPR.cloud REST) ---
async Task Balance()
{
    var cfg = Config();
    var rest = cfg["CsprCloudBaseUrl"];
    var agentHex = File.ReadAllText(Path.Combine("secrets", "agent", "public_key_hex")).Trim();
    using var http = AuthedHttp(cfg);
    var json = await http.GetStringAsync($"{rest}/accounts/{agentHex}");
    using var doc = JsonDocument.Parse(json);
    var d = doc.RootElement.GetProperty("data");
    var motes = d.GetProperty("balance").GetString();
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var cspr = decimal.Parse(motes!, inv) / 1_000_000_000m;
    Console.WriteLine($"Agent account_hash : {d.GetProperty("account_hash").GetString()}");
    Console.WriteLine($"Balance            : {cspr.ToString("N0", inv)} CSPR ({motes} motes)");
    Console.WriteLine($"Thresholds         : deployment={d.GetProperty("deployment_threshold").GetInt32()}, key_management={d.GetProperty("key_management_threshold").GetInt32()}");
}

void Help()
{
    Console.WriteLine("CHAINLEASH spike harness — commands:");
    Console.WriteLine("  keygen   smoke   balance   setup-keys   attempt-overreach   cosign-op   help");
    Console.WriteLine("Run:  dotnet run -- <command>");
}
