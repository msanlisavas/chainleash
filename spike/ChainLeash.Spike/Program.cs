// CHAINLEASH — Week-1 de-risking spike harness.
// See docs/superpowers/plans/2026-06-05-chainleash-foundation-spike.md
//
// Usage:  dotnet run -- <command>
//   keygen             Generate agent + human ED25519 key pairs into secrets/
//   smoke              Verify node RPC + config (prints node API/build version)
//   balance            Show the agent account balance + thresholds (via CSPR.cloud REST)
//   setup-keys         [Spike A] add human key (w3) + raise key_management threshold to 3
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

// HttpClient carrying the CSPR.cloud access key (node RPC + REST).
HttpClient AuthedHttp(IConfiguration cfg)
{
    var http = new HttpClient();
    var key = cfg["CsprCloudAccessKey"];
    if (!string.IsNullOrWhiteSpace(key) && !key.StartsWith("<"))
        http.DefaultRequestHeaders.Add("Authorization", key);
    return http;
}

NetCasperClient Rpc(IConfiguration cfg) => new(cfg["NodeRpcUrl"], AuthedHttp(cfg));

switch (command)
{
    case "keygen": Keygen(); break;
    case "smoke": await Smoke(); break;
    case "balance": await Balance(); break;
    case "setup-keys": await SetupKeys(); break;

    // Implemented next, after Spike A lands (Plan 1, Tasks 4-5).
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
    var status = (await Rpc(cfg).GetNodeStatus()).Parse();
    Console.WriteLine($"Node reachable: {nodeUrl}");
    Console.WriteLine($"API {status.ApiVersion} | build {status.BuildVersion} | chainspec {status.ChainspecName}");
    Console.WriteLine($"Config ChainName: {cfg["ChainName"]}");
}

// --- balance: agent account balance + current thresholds (CSPR.cloud REST) ---
async Task Balance()
{
    var cfg = Config();
    using var http = AuthedHttp(cfg);
    var (motes, dep, km, hash) = await AccountState(http, cfg["CsprCloudBaseUrl"]!, AgentHex(cfg));
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var cspr = decimal.Parse(motes, inv) / 1_000_000_000m;
    Console.WriteLine($"Agent account_hash : {hash}");
    Console.WriteLine($"Balance            : {cspr.ToString("N0", inv)} CSPR ({motes} motes)");
    Console.WriteLine($"Thresholds         : deployment={dep}, key_management={km}");
}

// --- [Spike A] setup-keys: add human key (w3) + raise key_management threshold to 3 ---
async Task SetupKeys()
{
    var cfg = Config();
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);
    var humanPk = PublicKey.FromHexString(File.ReadAllText(Path.Combine("secrets", "human", "public_key_hex")).Trim());

    var wasmPath = cfg["KeyManagerWasmPath"]
        ?? "../../contracts/key-manager-session/target/wasm32-unknown-unknown/release/key_manager.wasm";
    if (!File.Exists(wasmPath)) { Console.WriteLine($"wasm not found: {wasmPath} — build the contract first."); return; }
    var wasm = File.ReadAllBytes(wasmPath);

    var runtimeArgs = new List<NamedArg>
    {
        new NamedArg("human_key", humanPk),
        new NamedArg("human_weight", (byte)3),
        new NamedArg("deploy_threshold", (byte)1),
        new NamedArg("keymgmt_threshold", (byte)3),
    };

    var tx = new Transaction.SessionBuilder()
        .From(agentKp.PublicKey)
        .Wasm(wasm)
        .RuntimeArgs(runtimeArgs)
        .ChainName(cfg["ChainName"]!)
        .Payment(10_000_000_000UL, 1) // 10 CSPR limit, gas-price tolerance 1
        .Build();
    tx.Sign(agentKp);

    Console.WriteLine("Submitting key-management session (add human key w3 + key_management threshold -> 3)...");
    try
    {
        await Rpc(cfg).PutTransaction(tx);
        Console.WriteLine($"Accepted. tx hash: {tx.Hash}");
        Console.WriteLine($"Inspect: https://testnet.cspr.live/transaction/{tx.Hash}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PutTransaction REJECTED (pre-inclusion): {ex.Message}");
        return;
    }

    Console.WriteLine("Polling account until key_management threshold flips to 3...");
    using var http = AuthedHttp(cfg);
    for (int i = 1; i <= 30; i++)
    {
        await Task.Delay(5000);
        try
        {
            var (_, dep, km, _) = await AccountState(http, cfg["CsprCloudBaseUrl"]!, AgentHex(cfg));
            Console.WriteLine($"  [{i * 5,3}s] deployment={dep}, key_management={km}");
            if (km >= 3)
            {
                Console.WriteLine("OK Weighted keys configured — agent is now BELOW key_management threshold.");
                return;
            }
        }
        catch (Exception ex) { Console.WriteLine($"  poll error: {ex.Message}"); }
    }
    Console.WriteLine("Timed out — inspect the tx on cspr.live for the execution result.");
}

// --- helpers ---
string AgentHex(IConfiguration cfg) => File.ReadAllText(Path.Combine("secrets", "agent", "public_key_hex")).Trim();

async Task<(string motes, int dep, int km, string hash)> AccountState(HttpClient http, string restBase, string pubKeyHex)
{
    var json = await http.GetStringAsync($"{restBase}/accounts/{pubKeyHex}");
    using var doc = JsonDocument.Parse(json);
    var d = doc.RootElement.GetProperty("data");
    return (
        d.GetProperty("balance").GetString()!,
        d.GetProperty("deployment_threshold").GetInt32(),
        d.GetProperty("key_management_threshold").GetInt32(),
        d.GetProperty("account_hash").GetString()!);
}

void Help()
{
    Console.WriteLine("CHAINLEASH spike harness — commands:");
    Console.WriteLine("  keygen   smoke   balance   setup-keys   attempt-overreach   cosign-op   help");
    Console.WriteLine("Run:  dotnet run -- <command>");
}
