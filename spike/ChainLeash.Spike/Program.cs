// CHAINLEASH — Week-1 de-risking spike harness.
// See docs/superpowers/plans/2026-06-05-chainleash-foundation-spike.md
//
// Usage:  dotnet run -- <command>
// Run `dotnet run -- help` for the full grouped command list (key setup, weighted-key
// spikes, agent/owner vault ops, read-only).

using Casper.Network.SDK;
using Casper.Network.SDK.Types;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

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

try
{
    switch (command)
    {
        case "keygen": Keygen(); break;
        case "smoke": await Smoke(); break;
        case "balance": await Balance(); break;
        case "account-hash": AccountHashes(); break;
        case "vault-tighten": await VaultTighten(); break;
        case "vault-deploy": await VaultDeploy(); break;
        case "vault-upgrade": await VaultDeploy(upgrade: true, pkgHashHex: args.Length > 1 ? args[1] : null); break;
        case "vault-find": await VaultFind(); break;
        case "vault-init": await VaultInit(); break;
        case "vault-set-validator": await VaultSetValidator(); break;
        case "vault-state": await VaultState(); break;
        case "vault-bond": await VaultBond(); break;
        case "vault-slash": await VaultSlash(); break;
        case "vault-return-bond": await VaultReturnBond(); break;
        case "vault-transfer-owner": await VaultTransferOwner(); break;
        case "vault-set-agent": await VaultSetAgent(); break;
        case "vault-pause": await VaultPause(); break;
        case "vault-set-maxval": await VaultSetMaxVal(); break;
        case "vault-set-commission": await VaultSetCommission(); break;
        case "vault-set-interval": await VaultSetInterval(); break;
        case "vault-deposit": await VaultDeposit(); break;
        case "vault-delegate": await VaultDelegate(); break;
        case "vault-undelegate": await VaultUndelegate(); break;
        case "vault-redelegate": await VaultRedelegate(); break;
        case "vault-propose": await VaultPropose(); break;
        case "vault-approve": await VaultApprove(); break;
        case "vault-reject": await VaultReject(); break;
        case "vault-clear-committed": await VaultClearCommitted(); break;
        case "cosign-prepare": await CosignPrepare(); break;
        case "fund": await Fund(); break;
        case "setup-keys": await SetupKeys(); break;

        case "attempt-overreach": await AttemptOverreach(); break;

        case "cosign-op": await CosignOp(); break;

        default: Help(); break;
    }
}
catch (Exception ex)
{
    // One graceful floor — handlers print their own contextual errors; anything that
    // escapes (bad args, missing keys, RPC failures) lands here instead of a stack trace.
    Console.WriteLine($"error: {ex.Message}");
    if (ex is FileNotFoundException or DirectoryNotFoundException)
        Console.WriteLine("hint: run `dotnet run -- keygen` first or check Config/settings.local.json");
    Environment.ExitCode = 1;
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
    var humanPk = HumanPk(cfg);

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
    var accountPk = AgentPk(cfg);

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
// Identities derive from the configured secret PEMs when present, so the secret/public
// keys can never diverge — but the HUMAN/owner identity must also work from the public
// hex file alone: the owner's secret normally lives in their wallet, not on this machine
// (vault-init, cosign-prepare and account-hash only need the PUBLIC key).
PublicKey AgentPk(IConfiguration cfg) => PkFrom(cfg["AgentSecretKeyPath"]!, "secrets/agent/public_key_hex", "agent");
PublicKey HumanPk(IConfiguration cfg) => PkFrom(cfg["HumanSecretKeyPath"]!, "secrets/human/public_key_hex", "human");
string AgentHex(IConfiguration cfg) => AgentPk(cfg).ToAccountHex();

PublicKey PkFrom(string pemPath, string hexPath, string who)
{
    if (File.Exists(pemPath)) return KeyPair.FromPem(pemPath).PublicKey;
    if (File.Exists(hexPath)) return PublicKey.FromHexString(File.ReadAllText(hexPath).Trim());
    throw new FileNotFoundException($"no {who} key found — run `dotnet run -- keygen` first (looked for {pemPath} and {hexPath})");
}

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
    var cfg = Config();
    foreach (var (name, pk) in new[] { ("agent", AgentPk(cfg)), ("human", HumanPk(cfg)) })
        Console.WriteLine($"{name,-6} {pk.GetAccountHash()}");
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
    var humanPk = HumanPk(cfg);

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
    Console.WriteLine($"Initializing GovernedVault (agent, owner, value_cap={capMotes / 1_000_000_000m:N0} CSPR)...");
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
    var agentPk = AgentPk(cfg);
    var res = (await Rpc(cfg).GetAccountInfo(agentPk, (string?)null)).Parse();
    foreach (var nk in res.Account.NamedKeys)
        Console.WriteLine($"{nk.Name} = {nk.Key}");
}

// Install the Odra GovernedVault wasm via a TransactionV1 session (the proven pipeline).
// Replicates Odra's installer args: odra_cfg_* + the init constructor args.
async Task VaultDeploy(bool upgrade = false, string? pkgHashHex = null)
{
    var cfg = Config();
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);
    var wasmPath = cfg["GovernedVaultWasmPath"] ?? "../../contracts/governed_vault/wasm/GovernedVault.wasm";
    if (!File.Exists(wasmPath)) { Console.WriteLine($"wasm not found: {wasmPath} — build it first."); return; }
    var wasm = File.ReadAllBytes(wasmPath);

    // upgrade=true adds a NEW contract version to the EXISTING package (same
    // package_hash_key_name) so state (committed, delegations, bond, owner) is preserved;
    // upgrade=false is a fresh install. A failed upgrade is non-destructive — the current
    // version keeps serving.
    var rargs = new List<NamedArg>
    {
        new NamedArg("odra_cfg_package_hash_key_name", "governed_vault_package_hash"),
        new NamedArg("odra_cfg_allow_key_override", true),
        new NamedArg("odra_cfg_is_upgradable", true),
        new NamedArg("odra_cfg_is_upgrade", upgrade),
    };
    if (upgrade)
    {
        // Odra 2.7's upgrade_contract() reads these via RAW casper get_named_arg — the install path
        // never touches them, so a fresh install omits them, but an upgrade reverts with
        // ApiError::MissingArgument [2] without them. package_hash_to_upgrade is the existing
        // package's 32-byte HashAddr; create_upgrade_group=false because the upgrade group already
        // exists from the original Odra install (true is only for upgrading a non-Odra contract).
        if (string.IsNullOrWhiteSpace(pkgHashHex))
        {
            Console.WriteLine("vault-upgrade needs the existing package hash: dotnet run -- vault-upgrade <hash-…>");
            return;
        }
        var upgradeBytes = Convert.FromHexString(pkgHashHex.Replace("hash-", ""));
        rargs.Add(new NamedArg("odra_cfg_package_hash_to_upgrade", CLValue.ByteArray(upgradeBytes)));
        rargs.Add(new NamedArg("odra_cfg_create_upgrade_group", false));
    }

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
    Console.WriteLine($"{(upgrade ? "Upgrading" : "Installing")} GovernedVault ({wasm.Length} bytes)...");
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
// new_cap is REQUIRED — no default; tightening is a destructive change to the live cap.
// usage: vault-tighten <package-hash> <newCapMotes>
async Task VaultTighten()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-tighten <package-hash> <newCapMotes>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var newCap = ulong.Parse(args[2]);
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

// Read the vault's full state from chain with ZERO gas — Odra stores each field in a
// "state" dictionary keyed by blake2b(index_bytes ++ mapping_data); a top-level field at
// declaration index i uses index_bytes = [0,0,0,i].
// usage: vault-state <package-hash> [validatorHex ...]
async Task VaultState()
{
    var cfg = Config();
    if (args.Length < 2) { Console.WriteLine("usage: vault-state <package-hash> [validatorHex ...]"); return; }
    var pkg = args[1].Replace("hash-", "");
    using var http = AuthedHttp(cfg);
    var node = cfg["NodeRpcUrl"]!;

    async Task<JsonElement> Rpc(string method, object prms)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = prms });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = null; // node.testnet.casper.network rejects the "; charset=utf-8" param (400)
        var resp = await http.PostAsync(node, content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) // e.g. 429 returns a non-JSON body — don't choke on it
            throw new Exception($"RPC {method}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} — {text.Trim()}");
        var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            var msg = err.GetProperty("message").GetString();
            if (err.TryGetProperty("data", out var data)) msg += $" — {data.GetRawText()}"; // e.g. "ValueNotFound: …"
            throw new Exception($"RPC {method}: {msg}");
        }
        return doc.RootElement.GetProperty("result").Clone();
    }
    async Task<JsonElement> Query(string key) =>
        await Rpc("query_global_state", new { state_identifier = (object?)null, key, path = Array.Empty<string>() });

    // package -> latest contract hash -> named keys (state dict + main purse)
    var pkgSv = (await Query($"hash-{pkg}")).GetProperty("stored_value");
    string contractHash;
    if (pkgSv.TryGetProperty("ContractPackage", out var cp))
    {
        var versions = cp.GetProperty("versions").EnumerateArray().ToList();
        contractHash = versions[^1].GetProperty("contract_hash").GetString()!.Replace("contract-", "");
    }
    else { Console.WriteLine("not a contract package"); return; }
    var contract = (await Query($"hash-{contractHash}")).GetProperty("stored_value").GetProperty("Contract");
    string Named(string n) => contract.GetProperty("named_keys").EnumerateArray()
        .First(k => k.GetProperty("name").GetString() == n).GetProperty("key").GetString()!;
    var stateUref = Named("state");
    var mainPurse = Named("__contract_main_purse");
    var srh = (await Rpc("chain_get_state_root_hash", new { })).GetProperty("state_root_hash").GetString()!;

    string Blake2bHex(byte[] data)
    {
        var d = new Org.BouncyCastle.Crypto.Digests.Blake2bDigest(256);
        d.BlockUpdate(data, 0, data.Length);
        var hash = new byte[32]; d.DoFinal(hash, 0);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    byte[] IndexBytes(byte i) => new byte[] { 0, 0, 0, i };
    // Odra stores each Var as a CLValue::Any holding the field's raw bytesrepr; the RPC
    // returns those bytes in "parsed". Field indices are 1-based (declaration order +1).
    async Task<byte[]?> ReadBytes(byte index, byte[]? mapKey = null)
    {
        var input = mapKey is null ? IndexBytes(index) : IndexBytes(index).Concat(mapKey).ToArray();
        var itemKey = Blake2bHex(input);
        JsonElement r;
        try
        {
            r = await Rpc("state_get_dictionary_item", new
            {
                state_root_hash = srh,
                dictionary_identifier = new { URef = new { seed_uref = stateUref, dictionary_item_key = itemKey } }
            });
        }
        // ONLY the node's "value not found" means an unset field (renders as 0/false).
        // Anything else (HTTP 429, transport, parse) propagates to Field() below so a
        // rate-limited read never masquerades as an empty vault. The live node phrases it
        // "Query failed — value was not found in the global state"; match the known variants.
        catch (Exception ex) when (ex.Message.Contains("ValueNotFound", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("value not found", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("value was not found", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("failed to find", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var parsed = r.GetProperty("stored_value").GetProperty("CLValue").GetProperty("parsed");
        if (parsed.ValueKind != JsonValueKind.Array) return null;
        return parsed.EnumerateArray().Select(e => e.GetByte()).ToArray();
    }
    // Print one field; an RPC failure surfaces loudly per field instead of a silent zero.
    async Task Field(string label, Func<Task<string>> render)
    {
        try { Console.WriteLine($"{label}: {await render()}"); }
        catch (Exception ex) { Console.WriteLine($"{label}: <read failed: {ex.Message}>"); }
    }
    async Task<string> Balance(string uref) =>
        (await Rpc("query_balance", new { purse_identifier = new { purse_uref = uref } })).GetProperty("balance").GetString()!;

    // bytesrepr parsers: U512 = [len][len LE bytes]; u32 = 4 LE bytes; bool = 1 byte.
    decimal U512Cspr(byte[]? b) {
        if (b is null || b.Length == 0) return 0;
        int n = b[0]; System.Numerics.BigInteger v = 0;
        for (int i = 0; i < n && 1 + i < b.Length; i++) v += (System.Numerics.BigInteger)b[1 + i] << (8 * i);
        return (decimal)v / 1_000_000_000m;
    }
    uint U32(byte[]? b) => b is null || b.Length < 4 ? 0u : (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    bool Bool(byte[]? b) => b is not null && b.Length > 0 && b[0] == 1;
    decimal Motes(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture) / 1_000_000_000m;

    Console.WriteLine($"--- GovernedVault {pkg[..12]}… (read from chain, no gas) ---");
    Console.WriteLine($"contract         : {contractHash[..12]}…");
    decimal? total = null, bond = null;
    await Field("value_cap        ", async () => $"{U512Cspr(await ReadBytes(3)):N0} CSPR/action");
    await Field("paused           ", async () => $"{Bool(await ReadBytes(11))}");
    await Field("max_per_validator", async () => $"{U512Cspr(await ReadBytes(12)):N0} CSPR (0 = unlimited)");
    await Field("next_proposal_id ", async () => $"{U32(await ReadBytes(6))}");
    await Field("violations       ", async () => $"{U32(await ReadBytes(5))}");
    await Field("bond             ", async () => { bond = U512Cspr(await ReadBytes(4)); return $"{bond:N0} CSPR"; });
    await Field("total balance    ", async () => { total = Motes(await Balance(mainPurse)); return $"{total:N0} CSPR (liquid, incl. bond)"; });
    Console.WriteLine(total is decimal t && bond is decimal b
        ? $"free (withdrawable): {t - b:N0} CSPR"
        : "free (withdrawable): <read failed: needs bond + total balance>");
    // committed stake per validator: trailing CLI args > "Validators" in settings > demo pair
    var validators = args.Skip(2).ToArray();
    if (validators.Length == 0)
        validators = cfg.GetSection("Validators").GetChildren().Select(c => c.Value!).ToArray();
    if (validators.Length == 0)
    {
        Console.WriteLine("(demo default validators)");
        validators = new[]
        {
            "0106618e1493f73ee0bc67ffbad4ba4e3863b995d61786d9b9a68ec7676f697981",
            "017d96b9a63abcb61c870a4f55187a0a7ac24096bdb5fc585c12a686a4d892009e",
        };
    }
    foreach (var v in validators)
        await Field($"committed[{v[..10]}…]", async () => $"{U512Cspr(await ReadBytes(15, Convert.FromHexString(v))):N0} CSPR");
}

// Post the agent's slashable bond into the vault (payable proxy, like deposit_treasury).
// usage: vault-bond <package-hash> <motes>
async Task VaultBond()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-bond <package-hash> <motes>"); return; }
    var pkgBytes = Convert.FromHexString(args[1].Replace("hash-", ""));
    var motes = ulong.Parse(args[2]);
    var agentKp = KeyPair.FromPem(cfg["AgentSecretKeyPath"]!);
    var proxyPath = cfg["ProxyCallerWasmPath"] ?? "../../contracts/governed_vault/wasm/proxy_caller.wasm";
    if (!File.Exists(proxyPath)) { Console.WriteLine($"proxy wasm not found: {proxyPath}"); return; }
    var proxy = File.ReadAllBytes(proxyPath);
    var innerArgs = CLValue.List(new[] { CLValue.U8(0), CLValue.U8(0), CLValue.U8(0), CLValue.U8(0) });
    var rargs = new List<NamedArg>
    {
        new NamedArg("package_hash", CLValue.ByteArray(pkgBytes)),
        new NamedArg("entry_point", CLValue.String("deposit_bond")),
        new NamedArg("args", innerArgs),
        new NamedArg("attached_value", CLValue.U512(motes)),
        new NamedArg("amount", CLValue.U512(motes)),
    };
    var tx = new Transaction.SessionBuilder()
        .From(agentKp.PublicKey).Wasm(proxy).RuntimeArgs(rargs)
        .ChainName(cfg["ChainName"]!).Payment(20_000_000_000UL, 1).Build();
    tx.Sign(agentKp);
    await Submit(Rpc(cfg), tx, $"Agent posts a {motes / 1_000_000_000m:N0} CSPR slashable bond");
}

// Owner slashes (forfeits) part of the agent's bond on a violation. usage: vault-slash <pkg> <motes> <reason>
async Task VaultSlash()
{
    var cfg = Config();
    if (args.Length < 4) { Console.WriteLine("usage: vault-slash <pkg> <motes> <reason>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var motes = ulong.Parse(args[2]);
    var reason = args[3];
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null).EntryPoint("slash_bond")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("amount", CLValue.U512(motes)), new NamedArg("reason", CLValue.String(reason)) })
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(10_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner slashes {motes / 1_000_000_000m:N0} CSPR of the bond ({reason})");
}

// Owner returns the remaining bond to the operator. usage: vault-return-bond <pkg>
async Task VaultReturnBond()
{
    var cfg = Config();
    if (args.Length < 2) { Console.WriteLine("usage: vault-return-bond <pkg>"); return; }
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(args[1].Replace("hash-", ""), null, null).EntryPoint("return_bond")
        .RuntimeArgs(new List<NamedArg>())
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(10_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, "Owner returns the remaining bond to the operator");
}

// Owner transfers ownership. usage: vault-transfer-owner <pkg> <newOwnerHex>
async Task VaultTransferOwner()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-transfer-owner <pkg> <newOwnerHex>"); return; }
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var newOwner = PublicKey.FromHexString(args[2]);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(args[1].Replace("hash-", ""), null, null).EntryPoint("transfer_ownership")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("new_owner", CLValue.KeyFromPublicKey(newOwner)) })
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(10_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner transfers ownership to {args[2][..12]}…");
}

// Owner rotates the agent key. usage: vault-set-agent <pkg> <newAgentHex>
async Task VaultSetAgent()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-set-agent <pkg> <newAgentHex>"); return; }
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var newAgent = PublicKey.FromHexString(args[2]);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(args[1].Replace("hash-", ""), null, null).EntryPoint("set_agent")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("new_agent", CLValue.KeyFromPublicKey(newAgent)) })
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(10_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner rotates the agent key to {args[2][..12]}…");
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

// Owner sets the agent commission threshold (whole percent). usage: vault-set-commission <pkg> <percent>
async Task VaultSetCommission()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-set-commission <package-hash> <percent>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var pct = uint.Parse(args[2]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);
    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null).EntryPoint("set_max_commission")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("percent", CLValue.U32(pct)) })
        .From(humanKp.PublicKey).ChainName(cfg["ChainName"]!).Payment(5_000_000_000UL, 1).Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner sets commission threshold = {pct}%");
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

// Owner (human key, weight 3) REJECTS a pending material proposal WITHOUT executing it —
// the cleanup path for bad or stale agent proposals.
// usage: vault-reject <package-hash> <id>
async Task VaultReject()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-reject <package-hash> <id>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var id = uint.Parse(args[2]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("reject_material")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("id", CLValue.U32(id)) })
        .From(humanKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(5_000_000_000UL, 1) // resolves only — no (un)delegation, no auction interaction
        .Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner REJECTS material proposal #{id} → resolved without executing");
}

// Owner (human key) reconciles STALE `committed` for a validator that left the auction (withdrew
// its bid) — zeroes the phantom directed stake with no auction call, moves no CSPR.
// usage: vault-clear-committed <package-hash> <validatorHex>
async Task VaultClearCommitted()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: vault-clear-committed <package-hash> <validatorHex>"); return; }
    var pkg = args[1].Replace("hash-", "");
    var validator = PublicKey.FromHexString(args[2]);
    var humanKp = KeyPair.FromPem(cfg["HumanSecretKeyPath"]!);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("owner_clear_committed")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("validator", CLValue.PublicKey(validator)) })
        .From(humanKp.PublicKey)
        .ChainName(cfg["ChainName"]!)
        .Payment(5_000_000_000UL, 1) // ledger correction only — no auction interaction
        .Build();
    tx.Sign(humanKp);
    await Submit(Rpc(cfg), tx, $"Owner CLEARS stale committed for {args[2][..12]}… → phantom directed stake zeroed");
}

// Build the UNSIGNED approve_material(id) tx for the owner to sign in their OWN browser
// wallet (CSPR.click / Casper Wallet) — printed wallet-shaped, nothing signed or submitted.
// usage: cosign-prepare <package-hash> <id> [ownerPubkeyHex]
async Task CosignPrepare()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: cosign-prepare <package-hash> <id> [ownerPubkeyHex]"); return; }
    var pkg = args[1].Replace("hash-", "");
    var id = uint.Parse(args[2]);
    var ownerHex = args.Length > 3 ? args[3] : HumanPk(cfg).ToAccountHex();
    var owner = PublicKey.FromHexString(ownerHex);

    var tx = new Transaction.ContractCallBuilder()
        .ByPackageHash(pkg, null, null)
        .EntryPoint("approve_material")
        .RuntimeArgs(new List<NamedArg> { new NamedArg("id", CLValue.U32(id)) })
        .From(owner)
        .ChainName(cfg["ChainName"]!)
        .Payment(30_000_000_000UL, 1)
        .Build(); // NOT signed — the owner's wallet adds the signature in-browser

    // The C# SDK serializes a Transaction as {"Deploy":null,"Version1":{…}}. Lift the
    // Version1 node and wrap it the way the wallet SDKs consume it.
    var root = JsonNode.Parse(JsonSerializer.Serialize(tx))!.AsObject();
    var v1 = root["Version1"]!.DeepClone();
    var wrapped = new JsonObject { ["transaction"] = new JsonObject { ["Version1"] = v1 } };
    Console.WriteLine($"--- unsigned approve_material(#{id}) for owner {ownerHex[..12]}… (wallet-shaped) ---");
    Console.WriteLine(wrapped.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    await Task.CompletedTask;
}

// Native CSPR transfer from the agent to another account (e.g. top up the human for gas).
// usage: fund <agent|human|HEX> <motes>
async Task Fund()
{
    var cfg = Config();
    if (args.Length < 3) { Console.WriteLine("usage: fund <agent|human|HEX> <motes>"); return; }
    var targetHex = args[1] switch
    {
        "agent" => AgentHex(cfg),
        "human" => HumanPk(cfg).ToAccountHex(),
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
    void Line(string usage, string desc) => Console.WriteLine($"  {usage,-66} {desc}");
    Console.WriteLine("CHAINLEASH spike harness — usage: dotnet run -- <command>");
    Console.WriteLine();
    Console.WriteLine("key setup:");
    Line("keygen", "Generate agent + human ED25519 key pairs into secrets/");
    Line("account-hash", "Print the agent + human account hashes (vault agent/owner addresses)");
    Line("fund <agent|human|HEX> <motes>", "Native CSPR transfer from the agent (e.g. top up the human for gas)");
    Console.WriteLine();
    Console.WriteLine("weighted-key spikes:");
    Line("setup-keys", "[Spike A] add human key (w3) + raise key_management threshold to 3");
    Line("attempt-overreach", "[Spike B] agent-only key_management op — capture what the network returns");
    Line("cosign-op", "[Spike C] human/weighted co-sign success path");
    Console.WriteLine();
    Console.WriteLine("agent vault ops (signed by the agent key):");
    Line("vault-deploy", "Install the GovernedVault wasm (TransactionV1 session, ~500 CSPR gas)");
    Line("vault-upgrade", "Upgrade the GovernedVault in place — same package, preserves state (~500 CSPR gas)");
    Line("vault-init <package-hash> [capMotes]", "Initialize the deployed vault (agent/owner/value_cap)");
    Line("vault-deposit <package-hash> <motes>", "Fund the vault's purse via the payable proxy");
    Line("vault-bond <package-hash> <motes>", "Post the agent's slashable bond into the vault");
    Line("vault-delegate <package-hash> <validatorHex> <motes>", "Delegate vault CSPR to an allowlisted validator (routine, ≤ cap)");
    Line("vault-undelegate <package-hash> <validatorHex> <motes>", "Undelegate stake (unbonds back to the VAULT, not the agent)");
    Line("vault-redelegate <package-hash> <fromHex> <toHex> <motes>", "Move stake between validators in a single native tx");
    Line("vault-propose <package-hash> <validatorHex> <motes> [undelegate]", "Propose an over-cap (material) move → awaits owner co-sign");
    Line("vault-tighten <package-hash> <newCapMotes>", "Lower the per-action cap (agent may only tighten)");
    Console.WriteLine();
    Console.WriteLine("owner vault ops (signed by the human key):");
    Line("vault-set-validator <package-hash> <validatorHex> [true|false]", "Allowlist (or de-list) a validator");
    Line("vault-approve <package-hash> <id>", "Co-sign a pending material proposal → executes on-chain");
    Line("vault-reject <package-hash> <id>", "Reject a pending material proposal WITHOUT executing it");
    Line("vault-clear-committed <package-hash> <validatorHex>", "Zero stale committed for a validator that left the auction (no funds move)");
    Line("vault-slash <pkg> <motes> <reason>", "Slash (forfeit) part of the agent's bond on a violation");
    Line("vault-return-bond <pkg>", "Return the remaining bond to the operator");
    Line("vault-pause <package-hash> <true|false>", "Kill-switch — pause/unpause all agent moves");
    Line("vault-set-maxval <package-hash> <motes>", "Set the per-validator stake ceiling (0 = unlimited)");
    Line("vault-set-interval <package-hash> <ms>", "Set the agent action cooldown in ms (0 = disabled)");
    Line("vault-set-agent <pkg> <newAgentHex>", "Rotate the agent key");
    Line("vault-transfer-owner <pkg> <newOwnerHex>", "Transfer ownership");
    Console.WriteLine();
    Console.WriteLine("read-only (nothing signed or submitted):");
    Line("smoke", "Verify node RPC + config (prints node API/build version)");
    Line("balance", "Agent account balance + thresholds (via CSPR.cloud REST)");
    Line("vault-find", "Print the agent's named keys (find the vault package hash)");
    Line("vault-state <package-hash> [validatorHex ...]", "Read the vault's full state from chain (zero gas)");
    Line("cosign-prepare <package-hash> <id> [ownerPubkeyHex]", "Build the UNSIGNED wallet-shaped approve_material tx");
    Line("help", "Show this help");
}
