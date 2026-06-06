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
    case "account-hash": AccountHashes(); break;
    case "vault-tighten": await VaultTighten(); break;
    case "vault-deploy": await VaultDeploy(); break;
    case "vault-find": await VaultFind(); break;
    case "vault-init": await VaultInit(); break;
    case "vault-set-validator": await VaultSetValidator(); break;
    case "vault-pause": await VaultPause(); break;
    case "vault-set-maxval": await VaultSetMaxVal(); break;
    case "vault-set-interval": await VaultSetInterval(); break;
    case "vault-deposit": await VaultDeposit(); break;
    case "vault-delegate": await VaultDelegate(); break;
    case "vault-undelegate": await VaultUndelegate(); break;
    case "vault-redelegate": await VaultRedelegate(); break;
    case "vault-propose": await VaultPropose(); break;
    case "vault-approve": await VaultApprove(); break;
    case "fund": await Fund(); break;
    case "setup-keys": await SetupKeys(); break;

    case "attempt-overreach": await AttemptOverreach(); break;

    case "cosign-op": await CosignOp(); break;

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

// --- [Spike B] attempt-overreach: agent (weight 1) tries to lower its own key_management threshold ---
async Task AttemptOverreach()
{
    var cfg = Config();
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);
    var wasmPath = cfg["OverreachWasmPath"]
        ?? "../../contracts/overreach-session/target/wasm32-unknown-unknown/release/overreach.wasm";
    if (!File.Exists(wasmPath)) { Console.WriteLine($"wasm not found: {wasmPath} — build the contract first."); return; }
    var wasm = File.ReadAllBytes(wasmPath);

    var tx = new Transaction.SessionBuilder()
        .From(agentKp.PublicKey)
        .Wasm(wasm)
        .RuntimeArgs(new List<NamedArg>())
        .ChainName(cfg["ChainName"]!)
        .Payment(5_000_000_000UL, 1)
        .Build();
    tx.Sign(agentKp); // ONLY the agent (weight 1) signs — below the key_management threshold of 3

    var client = Rpc(cfg);
    Console.WriteLine("Agent (weight 1) attempts to lower its OWN key_management threshold to 1 (unleash)...");
    try
    {
        await client.PutTransaction(tx);
        Console.WriteLine($"Accepted into the network. tx: {tx.Hash}");
        Console.WriteLine($"Inspect: https://testnet.cspr.live/transaction/{tx.Hash}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"RESULT: PRE-INCLUSION REJECT (not an on-chain tx) — {ex.Message}");
        return;
    }

    Console.WriteLine("Polling for the execution result...");
    for (int i = 1; i <= 24; i++)
    {
        await Task.Delay(5000);
        try
        {
            var res = (await client.GetTransaction(tx.Hash, System.Threading.CancellationToken.None)).Parse();
            var er = res.ExecutionInfo?.ExecutionResult;
            if (er is not null)
            {
                Console.WriteLine($"RESULT: EXECUTED on-chain (block {res.ExecutionInfo!.BlockHeight}) — IsSuccess={er.IsSuccess}");
                Console.WriteLine($"  cost={er.Cost}, consumed={er.Consumed}");
                if (!er.IsSuccess) Console.WriteLine($"  error_message: {er.ErrorMessage}");
                Console.WriteLine(er.IsSuccess
                    ? "  WARNING the agent SUCCEEDED — the leash FAILED (unexpected!)"
                    : "  OK EXECUTED-FAILED: the protocol rejected the over-reach. The rejection IS a real on-chain artifact.");
                return;
            }
            Console.WriteLine($"  [{i * 5,3}s] accepted, not yet executed...");
        }
        catch (Exception ex) { Console.WriteLine($"  poll: {ex.Message}"); }
    }
    Console.WriteLine("Timed out waiting for execution — check cspr.live.");
}

// --- [Spike C] cosign-op: human key (weight 3) performs a key-management op the agent cannot ---
async Task CosignOp()
{
    var cfg = Config();
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var accountPk = PublicKey.FromHexString(File.ReadAllText(Path.Combine("secrets", "agent", "public_key_hex")).Trim());

    var wasmPath = cfg["CosignWasmPath"]
        ?? "../../contracts/cosign-proof-session/target/wasm32-unknown-unknown/release/cosign_proof.wasm";
    if (!File.Exists(wasmPath)) { Console.WriteLine($"wasm not found: {wasmPath}"); return; }
    var wasm = File.ReadAllBytes(wasmPath);

    var tx = new Transaction.SessionBuilder()
        .From(accountPk)             // the treasury account identity (agent pubkey)
        .Wasm(wasm)
        .RuntimeArgs(new List<NamedArg>())
        .ChainName(cfg["ChainName"]!)
        .Payment(5_000_000_000UL, 1)
        .Build();
    tx.Sign(humanKp);                // signed by the HUMAN associated key (weight 3) ONLY

    var client = Rpc(cfg);
    Console.WriteLine("Human key (weight 3) performs a key-management op the agent was just denied...");
    try
    {
        await client.PutTransaction(tx);
        Console.WriteLine($"Accepted. tx: {tx.Hash}");
        Console.WriteLine($"Inspect: https://testnet.cspr.live/transaction/{tx.Hash}");
    }
    catch (Exception ex) { Console.WriteLine($"PutTransaction REJECTED: {ex.Message}"); return; }

    Console.WriteLine("Polling for the execution result...");
    for (int i = 1; i <= 24; i++)
    {
        await Task.Delay(5000);
        try
        {
            var res = (await client.GetTransaction(tx.Hash, System.Threading.CancellationToken.None)).Parse();
            var er = res.ExecutionInfo?.ExecutionResult;
            if (er is not null)
            {
                Console.WriteLine($"RESULT: EXECUTED (block {res.ExecutionInfo!.BlockHeight}) — IsSuccess={er.IsSuccess}");
                if (!er.IsSuccess) Console.WriteLine($"  error_message: {er.ErrorMessage}");
                Console.WriteLine(er.IsSuccess
                    ? "  OK Human co-sign authorized key management — weighted multi-sig works."
                    : "  WARNING Human op failed (unexpected).");
                return;
            }
            Console.WriteLine($"  [{i * 5,3}s] accepted, not yet executed...");
        }
        catch (Exception ex) { Console.WriteLine($"  poll: {ex.Message}"); }
    }
    Console.WriteLine("Timed out.");
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

// Print the agent + human account hashes (used as the vault agent/owner addresses).
void AccountHashes()
{
    foreach (var name in new[] { "agent", "human" })
    {
        var hex = File.ReadAllText(Path.Combine("secrets", name, "public_key_hex")).Trim();
        var pk = PublicKey.FromHexString(hex);
        Console.WriteLine($"{name,-6} {pk.GetAccountHash()}");
    }
}

// Initialize the deployed GovernedVault (agent/owner/value_cap) by package hash.
// usage: vault-init <package-hash>
async Task VaultInit()
{
    var cfg = Config();
    if (args.Length < 2) { Console.WriteLine("usage: vault-init <package-hash> [capMotes]"); return; }
    var pkg = args[1].Replace("hash-", "");
    var capMotes = args.Length > 2 ? ulong.Parse(args[2]) : 600_000_000_000UL; // 600 CSPR per-action cap
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);
    var humanPk = PublicKey.FromHexString(File.ReadAllText(Path.Combine("secrets", "human", "public_key_hex")).Trim());

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("initialize")
        .RuntimeArgs(new List<NamedArg>
        {
            new NamedArg("agent", CLValue.KeyFromPublicKey(agentKp.PublicKey)),
            new NamedArg("owner", CLValue.KeyFromPublicKey(humanPk)),
            new NamedArg("value_cap", CLValue.U512(capMotes)),
        })
        .From(agentKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(10_000_000_000UL, 1)
        .Build();
    tx.Sign(agentKp);

    var client = Rpc(cfg);
    Console.WriteLine("Initializing GovernedVault (agent, owner, value_cap=2 CSPR)...");
    try { await client.PutTransaction(tx); Console.WriteLine($"tx: {tx.Hash}\nhttps://testnet.cspr.live/transaction/{tx.Hash}"); }
    catch (Exception ex) { Console.WriteLine($"REJECTED: {ex.Message}"); return; }

    for (int i = 1; i <= 24; i++)
    {
        await Task.Delay(5000);
        try
        {
            var er = (await client.GetTransaction(tx.Hash, System.Threading.CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null)
            {
                Console.WriteLine($"EXECUTED IsSuccess={er.IsSuccess} cost={er.Cost}");
                if (!er.IsSuccess) Console.WriteLine($"  error: {er.ErrorMessage}");
                else Console.WriteLine("  OK GovernedVault initialized.");
                return;
            }
            Console.WriteLine($"  [{i * 5,3}s] pending...");
        }
        catch (Exception ex) { Console.WriteLine($"  poll: {ex.Message}"); }
    }
}

// Print the agent entity's named keys (to find the GovernedVault package hash).
async Task VaultFind()
{
    var cfg = Config();
    var agentPk = PublicKey.FromHexString(File.ReadAllText(Path.Combine("secrets", "agent", "public_key_hex")).Trim());
    var res = (await Rpc(cfg).GetAccountInfo(agentPk, (string?)null)).Parse();
    foreach (var nk in res.Account.NamedKeys)
        Console.WriteLine($"{nk.Name} = {nk.Key}");
}

// Install the Odra GovernedVault wasm via a TransactionV1 session (the proven pipeline).
// Replicates Odra's installer args: odra_cfg_* + the init constructor args.
async Task VaultDeploy()
{
    var cfg = Config();
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);
    var wasmPath = cfg["GovernedVaultWasmPath"] ?? "../../contracts/governed_vault/wasm/GovernedVault.wasm";
    if (!File.Exists(wasmPath)) { Console.WriteLine($"wasm not found: {wasmPath} — build it first."); return; }
    var wasm = File.ReadAllBytes(wasmPath);

    // No constructor -> install needs only the Odra cfg args; we initialize() separately.
    var rargs = new List<NamedArg>
    {
        new NamedArg("odra_cfg_package_hash_key_name", "governed_vault_package_hash"),
        new NamedArg("odra_cfg_allow_key_override", true),
        new NamedArg("odra_cfg_is_upgradable", true),
        new NamedArg("odra_cfg_is_upgrade", false),
    };

    var tx = new Transaction.SessionBuilder()
        .From(agentKp.PublicKey)
        .Wasm(wasm)
        .InstallOrUpgrade() // Casper 2.0: mark as the InstallUpgrade transaction category
        .RuntimeArgs(rargs)
        .ChainName(cfg["ChainName"]!)
        .Payment(500_000_000_000UL, 1) // 500 CSPR for the install (295KB wasm)
        .Build();
    tx.Sign(agentKp);

    var client = Rpc(cfg);
    Console.WriteLine($"Installing GovernedVault ({wasm.Length} bytes)...");
    try
    {
        await client.PutTransaction(tx);
        Console.WriteLine($"Accepted. tx: {tx.Hash}");
        Console.WriteLine($"https://testnet.cspr.live/transaction/{tx.Hash}");
    }
    catch (Exception ex) { Console.WriteLine($"REJECTED (pre-inclusion): {ex.Message}"); return; }

    for (int i = 1; i <= 30; i++)
    {
        await Task.Delay(5000);
        try
        {
            var er = (await client.GetTransaction(tx.Hash, System.Threading.CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null)
            {
                Console.WriteLine($"EXECUTED IsSuccess={er.IsSuccess} cost={er.Cost}");
                if (!er.IsSuccess) Console.WriteLine($"  error: {er.ErrorMessage}");
                else Console.WriteLine("  OK GovernedVault installed — find its package hash under the agent account's named keys.");
                return;
            }
            Console.WriteLine($"  [{i * 5,3}s] pending...");
        }
        catch (Exception ex) { Console.WriteLine($"  poll: {ex.Message}"); }
    }
    Console.WriteLine("Timed out.");
}

// Call tighten_cap on the deployed GovernedVault (agent-authorized, emits CapTightened).
// usage: vault-tighten <contract-hash> [newCapMotes]
async Task VaultTighten()
{
    var cfg = Config();
    if (args.Length < 2) { Console.WriteLine("usage: vault-tighten <contract-hash> [newCapMotes]"); return; }
    var pkg = args[1].Replace("hash-", "");
    var newCap = args.Length > 2 ? ulong.Parse(args[2]) : 1_000_000_000UL;
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("tighten_cap")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("new_cap", CLValue.U512(newCap)) })
        .From(agentKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(5_000_000_000UL, 1)
        .Build();
    tx.Sign(agentKp);

    var client = Rpc(cfg);
    Console.WriteLine($"Calling tighten_cap(new_cap={newCap}) on package {pkg}...");
    try
    {
        await client.PutTransaction(tx);
        Console.WriteLine($"tx: {tx.Hash}");
        Console.WriteLine($"https://testnet.cspr.live/transaction/{tx.Hash}");
    }
    catch (Exception ex) { Console.WriteLine($"REJECTED: {ex.Message}"); return; }

    for (int i = 1; i <= 24; i++)
    {
        await Task.Delay(5000);
        try
        {
            var er = (await client.GetTransaction(tx.Hash, System.Threading.CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null)
            {
                Console.WriteLine($"EXECUTED IsSuccess={er.IsSuccess} cost={er.Cost}");
                if (!er.IsSuccess) Console.WriteLine($"  err: {er.ErrorMessage}");
                else Console.WriteLine("  OK CapTightened emitted — stored-contract call works.");
                return;
            }
            Console.WriteLine($"  [{i * 5,3}s] pending...");
        }
        catch (Exception ex) { Console.WriteLine($"  poll: {ex.Message}"); }
    }
    Console.WriteLine("Timed out.");
}

// Owner kill-switch — pause/unpause all agent moves. usage: vault-pause <pkg> <true|false>
async Task VaultPause()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-pause <package-hash> <true|false>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var paused = bool.Parse(args[2]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null).EntryPoint("set_paused")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("paused", CLValue.Bool(paused)) })
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(5_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner sets kill-switch paused={paused}");
}

// Owner sets the per-validator stake ceiling (0 = unlimited). usage: vault-set-maxval <pkg> <motes>
async Task VaultSetMaxVal()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-set-maxval <package-hash> <motes>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var max = ulong.Parse(args[2]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null).EntryPoint("set_max_per_validator")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("max", CLValue.U512(max)) })
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(5_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner sets per-validator cap = {max / 1_000_000_000m:N0} CSPR");
}

// Owner sets the agent action cooldown in ms (0 = disabled). usage: vault-set-interval <pkg> <ms>
async Task VaultSetInterval()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-set-interval <package-hash> <ms>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var ms = ulong.Parse(args[2]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null).EntryPoint("set_action_interval")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("interval_ms", CLValue.U64(ms)) })
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(5_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner sets action cooldown = {ms} ms");
}

// Owner (human key, weight 3) allowlists a validator on the GovernedVault.
// usage: vault-set-validator <package-hash> <validatorHex> [true|false]
async Task VaultSetValidator()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-set-validator <package-hash> <validatorHex> [true|false]"); return; }
    var pkg = args[1].Replace("hash-", "");
    var validator = PublicKey.FromHexString(args[2]);
    var allowed = args.Length < 4 || bool.Parse(args[3]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("set_validator")
        .RuntimeArgs(new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("allowed", CLValue.Bool(allowed)),
        })
        .From(humanKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(5_000_000_000UL, 1)
        .Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner allowlists validator {args[2][..12]}… (allowed={allowed})");
}

// Fund the vault's purse via Odra's payable proxy (proxy_caller.wasm run as session).
// The proxy creates a cargo purse, moves `amount` from the agent's main purse into
// it, and forwards it to the contract's payable deposit_treasury entry point.
// usage: vault-deposit <package-hash> <motes>
async Task VaultDeposit()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-deposit <package-hash> <motes>"); return; }
    var pkgBytes = Convert.FromHexString(args[1].Replace("hash-", ""));
    var motes = ulong.Parse(args[2]);
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);

    var proxyPath = cfg["ProxyCallerWasmPath"] ?? "../../contracts/governed_vault/wasm/proxy_caller.wasm";
    if (!File.Exists(proxyPath)) { Console.WriteLine($"proxy wasm not found: {proxyPath}"); return; }
    var proxy = File.ReadAllBytes(proxyPath);

    // Inner args for deposit_treasury are empty → serialized RuntimeArgs = 4 zero bytes,
    // passed as List(U8) so the CLType matches casper's `Bytes` (List<u8>). The proxy
    // deserializes this, injects `cargo_purse`, and calls the contract.
    var innerArgs = CLValue.List(new[] { CLValue.U8(0), CLValue.U8(0), CLValue.U8(0), CLValue.U8(0) });

    var rargs = new List<NamedArg>
    {
        new NamedArg("package_hash", CLValue.ByteArray(pkgBytes)),
        new NamedArg("entry_point", CLValue.String("deposit_treasury")),
        new NamedArg("args", innerArgs),
        new NamedArg("attached_value", CLValue.U512(motes)),
        new NamedArg("amount", CLValue.U512(motes)),
    };

    var tx = new Transaction.SessionBuilder()
        .From(agentKp.PublicKey)
        .Wasm(proxy)
        .RuntimeArgs(rargs)
        .ChainName(cfg["ChainName"]!)
        .Payment(20_000_000_000UL, 1) // 20 CSPR gas; the deposited `amount` moves separately from the main purse
        .Build();
    tx.Sign(agentKp);
    await Submit(Rpc(cfg), tx, $"Funding vault purse with {motes / 1_000_000_000m:N0} CSPR via payable proxy");
}

// Agent delegates vault-held CSPR to an allowlisted validator (routine, ≤ cap).
// The contract delegates from ITS OWN purse — the decisive contract-custodial test.
// usage: vault-delegate <package-hash> <validatorHex> <motes>
async Task VaultDelegate()
{
    var cfg = Config();
    if (args.Length < 4) { Console.WriteLine("usage: vault-delegate <package-hash> <validatorHex> <motes>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var validator = PublicKey.FromHexString(args[2]);
    var motes = ulong.Parse(args[3]);
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("delegate")
        .RuntimeArgs(new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(motes)),
        })
        .From(agentKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(30_000_000_000UL, 1) // 30 CSPR gas (auction interaction)
        .Build();
    tx.Sign(agentKp);
    await Submit(Rpc(cfg), tx, $"Agent delegates {motes / 1_000_000_000m:N0} CSPR to {args[2][..12]}… (from vault purse)");
}

// Agent undelegates vault-held stake from a validator (routine, ≤ cap). Funds unbond
// back to the VAULT (not the agent). This is the "rebalance away" / exit path.
// usage: vault-undelegate <package-hash> <validatorHex> <motes>
async Task VaultUndelegate()
{
    var cfg = Config();
    if (args.Length < 4) { Console.WriteLine("usage: vault-undelegate <package-hash> <validatorHex> <motes>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var validator = PublicKey.FromHexString(args[2]);
    var motes = ulong.Parse(args[3]);
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("undelegate")
        .RuntimeArgs(new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(motes)),
        })
        .From(agentKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(30_000_000_000UL, 1)
        .Build();
    tx.Sign(agentKp);
    await Submit(Rpc(cfg), tx, $"Agent undelegates {motes / 1_000_000_000m:N0} CSPR from {args[2][..12]}… (unbonds back to vault)");
}

// Agent moves stake from one validator to another in a single native tx (the funds
// unbond from the old validator and auto-move to the new one — standard unbonding
// applies; no manual re-stake needed). Destination must be allowlisted; ≤ cap.
// usage: vault-redelegate <package-hash> <fromValidatorHex> <toValidatorHex> <motes>
async Task VaultRedelegate()
{
    var cfg = Config();
    if (args.Length < 5) { Console.WriteLine("usage: vault-redelegate <package-hash> <fromHex> <toHex> <motes>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var from = PublicKey.FromHexString(args[2]);
    var to = PublicKey.FromHexString(args[3]);
    var motes = ulong.Parse(args[4]);
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("redelegate")
        .RuntimeArgs(new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(from)),
            new NamedArg("new_validator", CLValue.PublicKey(to)),
            new NamedArg("amount", CLValue.U512(motes)),
        })
        .From(agentKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(30_000_000_000UL, 1)
        .Build();
    tx.Sign(agentKp);
    await Submit(Rpc(cfg), tx, $"Agent redelegates {motes / 1_000_000_000m:N0} CSPR {args[2][..10]}… → {args[3][..10]}… (single native tx)");
}

// Agent proposes an over-cap (material) (un)delegation — emits MaterialProposed,
// awaiting the owner's co-sign. The first proposal has id 0.
// usage: vault-propose <package-hash> <validatorHex> <motes> [undelegate]
async Task VaultPropose()
{
    var cfg = Config();
    if (args.Length < 4) { Console.WriteLine("usage: vault-propose <package-hash> <validatorHex> <motes> [undelegate]"); return; }
    var pkg = args[1].Replace("hash-", "");
    var validator = PublicKey.FromHexString(args[2]);
    var motes = ulong.Parse(args[3]);
    var undelegate = args.Length > 4 && bool.Parse(args[4]);
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("propose_material")
        .RuntimeArgs(new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(motes)),
            new NamedArg("undelegate", CLValue.Bool(undelegate)),
        })
        .From(agentKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(5_000_000_000UL, 1)
        .Build();
    tx.Sign(agentKp);
    await Submit(Rpc(cfg), tx, $"Agent proposes MATERIAL {(undelegate ? "undelegate" : "delegate")} {motes / 1_000_000_000m:N0} CSPR (over cap) → awaits owner co-sign");
}

// Owner (human key, weight 3) co-signs and executes a pending material proposal.
// usage: vault-approve <package-hash> <id>
async Task VaultApprove()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-approve <package-hash> <id>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var id = uint.Parse(args[2]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("approve_material")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("id", CLValue.U32(id)) })
        .From(humanKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(30_000_000_000UL, 1) // executes the (un)delegation → auction interaction
        .Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner co-signs material proposal #{id} → executes on-chain");
}

// Native CSPR transfer from the agent to another account (e.g. top up the human for gas).
// usage: fund <agent|human|HEX> <motes>
async Task Fund()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: fund <agent|human|HEX> <motes>"); return; }
    var targetHex = args[1] switch
    {
        "agent" => File.ReadAllText(Path.Combine("secrets", "agent", "public_key_hex")).Trim(),
        "human" => File.ReadAllText(Path.Combine("secrets", "human", "public_key_hex")).Trim(),
        var h => h,
    };
    var target = PublicKey.FromHexString(targetHex);
    var motes = ulong.Parse(args[2]);
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);

    var tx = new Transaction.NativeTransferBuilder()
        .From(agentKp.PublicKey)
        .Target(target)
        .Amount(motes)
        .ChainName(cfg["ChainName"]!)
        .Payment(100_000_000UL)
        .Build();
    tx.Sign(agentKp);
    await Submit(Rpc(cfg), tx, $"Transfer {motes / 1_000_000_000m:N2} CSPR to {args[1]}");
}

// Submit a transaction and poll for the on-chain execution result.
async Task Submit(NetCasperClient client, Transaction tx, string label)
{
    Console.WriteLine($"{label}...");
    try
    {
        await client.PutTransaction(tx);
        Console.WriteLine($"tx: {tx.Hash}");
        Console.WriteLine($"https://testnet.cspr.live/transaction/{tx.Hash}");
    }
    catch (Exception ex) { Console.WriteLine($"REJECTED (pre-inclusion): {ex.Message}"); return; }

    for (int i = 1; i <= 30; i++)
    {
        await Task.Delay(5000);
        try
        {
            var er = (await client.GetTransaction(tx.Hash, System.Threading.CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null)
            {
                Console.WriteLine($"EXECUTED IsSuccess={er.IsSuccess} cost={er.Cost}");
                if (!er.IsSuccess) Console.WriteLine($"  error: {er.ErrorMessage}");
                else Console.WriteLine("  OK");
                return;
            }
            Console.WriteLine($"  [{i * 5,3}s] pending...");
        }
        catch (Exception ex) { Console.WriteLine($"  poll: {ex.Message}"); }
    }
    Console.WriteLine("Timed out.");
}

void Help()
{
    Console.WriteLine("CHAINLEASH spike harness — commands:");
    Console.WriteLine("  keygen  smoke  balance  account-hash  setup-keys  attempt-overreach  cosign-op");
    Console.WriteLine("  vault-deploy  vault-find  vault-init  vault-set-validator  vault-deposit  vault-delegate  vault-tighten  fund  help");
    Console.WriteLine("Run:  dotnet run -- <command>");
}
