using System.Text;
using System.Text.Json;
using System.Numerics;
using Org.BouncyCastle.Crypto.Digests;

namespace ChainLeash.Agent;

/// Reads the GovernedVault's live state directly from chain with ZERO gas. Odra stores
/// each field in a "state" dictionary keyed by blake2b(index_bytes ++ mapping_data); a
/// top-level field at declaration index i (1-based) uses index_bytes [0,0,0,i], and the
/// stored value is the field's raw bytesrepr (U512 = [len][LE]; u32 = 4 LE; bool = 1 byte).
/// This lets the agent + dashboard operate on chain-truth instead of config seeds.
public sealed class ChainReader
{
    private readonly IConfiguration _cfg;
    private readonly string _node;
    private readonly string _pkg;
    private string? _stateUref;
    private string? _mainPurse;
    private string? _srh;
    private DateTime _srhAt;

    // GovernedVault field indices (declaration order, 1-based — Odra reserves index 0).
    private const byte IxValueCap = 3, IxBond = 4, IxViolations = 5, IxNextId = 6,
                       IxPaused = 11, IxMaxPerValidator = 12, IxCommitted = 15;

    public ChainReader(IConfiguration cfg)
    {
        _cfg = cfg;
        _node = cfg["Casper:NodeRpcUrl"]!;
        _pkg = cfg["Casper:GovernedVaultPackageHash"]!.Replace("hash-", "");
    }

    private HttpClient Http()
    {
        var h = new HttpClient();
        var key = _cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) h.DefaultRequestHeaders.Add("Authorization", key);
        return h;
    }

    private async Task<JsonElement> Rpc(string method, object prms)
    {
        using var http = Http();
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = prms });
        var resp = await http.PostAsync(_node, new StringContent(body, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new Exception($"RPC {method}: {err.GetProperty("message").GetString()}");
        return doc.RootElement.GetProperty("result").Clone();
    }

    private Task<JsonElement> Query(string key) =>
        Rpc("query_global_state", new { state_identifier = (object?)null, key, path = Array.Empty<string>() });

    private async Task EnsureResolved()
    {
        if (_stateUref is not null) return;
        var pkgSv = (await Query($"hash-{_pkg}")).GetProperty("stored_value");
        var versions = pkgSv.GetProperty("ContractPackage").GetProperty("versions").EnumerateArray().ToList();
        var contractHash = versions[^1].GetProperty("contract_hash").GetString()!.Replace("contract-", "");
        var contract = (await Query($"hash-{contractHash}")).GetProperty("stored_value").GetProperty("Contract");
        string Named(string n) => contract.GetProperty("named_keys").EnumerateArray()
            .First(k => k.GetProperty("name").GetString() == n).GetProperty("key").GetString()!;
        _stateUref = Named("state");
        _mainPurse = Named("__contract_main_purse");
    }

    private static string Blake2bHex(byte[] data)
    {
        var d = new Blake2bDigest(256);
        d.BlockUpdate(data, 0, data.Length);
        var hash = new byte[32];
        d.DoFinal(hash, 0);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Cache the state root hash briefly so a tick's many reads share one fetch.
    private async Task<string> StateRootHash()
    {
        if (_srh is null || (DateTime.UtcNow - _srhAt).TotalSeconds > 5)
        {
            _srh = (await Rpc("chain_get_state_root_hash", new { })).GetProperty("state_root_hash").GetString()!;
            _srhAt = DateTime.UtcNow;
        }
        return _srh;
    }

    private async Task<byte[]?> ReadBytes(byte index, byte[]? mapKey = null)
    {
        await EnsureResolved();
        var input = mapKey is null ? new byte[] { 0, 0, 0, index } : new byte[] { 0, 0, 0, index }.Concat(mapKey).ToArray();
        try
        {
            var srh = await StateRootHash();
            var r = await Rpc("state_get_dictionary_item", new
            {
                state_root_hash = srh,
                dictionary_identifier = new { URef = new { seed_uref = _stateUref, dictionary_item_key = Blake2bHex(input) } }
            });
            var parsed = r.GetProperty("stored_value").GetProperty("CLValue").GetProperty("parsed");
            if (parsed.ValueKind != JsonValueKind.Array) return null;
            return parsed.EnumerateArray().Select(e => e.GetByte()).ToArray();
        }
        catch { return null; } // unset field
    }

    private static decimal U512Cspr(byte[]? b)
    {
        if (b is null || b.Length == 0) return 0;
        int n = b[0];
        BigInteger v = 0;
        for (int i = 0; i < n && 1 + i < b.Length; i++) v += (BigInteger)b[1 + i] << (8 * i);
        return (decimal)v / 1_000_000_000m;
    }
    private static uint U32(byte[]? b) => b is null || b.Length < 4 ? 0u : (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    private static bool Bool(byte[]? b) => b is not null && b.Length > 0 && b[0] == 1;

    public async Task<decimal> ValueCapCspr() => U512Cspr(await ReadBytes(IxValueCap));
    public async Task<decimal> BondCspr() => U512Cspr(await ReadBytes(IxBond));
    public async Task<decimal> MaxPerValidatorCspr() => U512Cspr(await ReadBytes(IxMaxPerValidator));
    public async Task<bool> Paused() => Bool(await ReadBytes(IxPaused));
    public async Task<uint> NextProposalId() => U32(await ReadBytes(IxNextId));
    public async Task<uint> Violations() => U32(await ReadBytes(IxViolations));
    public async Task<decimal> CommittedCspr(string validatorHex) => U512Cspr(await ReadBytes(IxCommitted, Convert.FromHexString(validatorHex)));

    /// Total liquid CSPR in the vault purse (un-delegated; includes the bond).
    public async Task<decimal> TotalBalanceCspr()
    {
        await EnsureResolved();
        var bal = (await Rpc("query_balance", new { purse_identifier = new { purse_uref = _mainPurse } })).GetProperty("balance").GetString()!;
        return decimal.Parse(bal, System.Globalization.CultureInfo.InvariantCulture) / 1_000_000_000m;
    }

    /// Free, deployable CSPR = liquid balance minus the reserved bond.
    public async Task<decimal> FreeBalanceCspr() => await TotalBalanceCspr() - await BondCspr();
}
