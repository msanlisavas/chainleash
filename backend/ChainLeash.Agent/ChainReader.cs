using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Digests;

namespace ChainLeash.Agent;

/// An RPC failure with its error CLASS preserved. `NotFound` (a dictionary key that was
/// never written) is the ONLY class callers may treat as "field unset" — everything else
/// (HTTP 429 on the shared CSPR.cloud key, transport faults, non-JSON bodies) must
/// surface, or the agent would act on a fabricated leash state (kill-switch silently
/// reading as off, cap reading as 0).
public sealed class ChainRpcException(string method, string detail, bool notFound = false)
    : Exception($"RPC {method}: {detail}")
{
    public bool NotFound { get; } = notFound;
}

/// Reads the GovernedVault's live state directly from chain with ZERO gas. Odra stores
/// each field in a "state" dictionary keyed by blake2b(index_bytes ++ mapping_data); a
/// top-level field at declaration index i (1-based) uses index_bytes [0,0,0,i], and the
/// stored value is the field's raw bytesrepr (see OdraBytes).
/// This lets the agent + dashboard operate on chain-truth instead of config seeds.
public sealed class ChainReader
{
    private readonly string _node;
    private readonly string _pkg;
    private readonly HttpClient _http; // one shared client — a fresh one per RPC churns sockets in a 24/7 daemon
    private string? _stateUref;
    private string? _mainPurse;
    private string? _srh;
    private DateTime _srhAt;
    // Rarely-changing, on-chain-ENFORCED config fields get a long TTL so the per-tick loop doesn't
    // re-read owner-set values that change only on (rare) owner action.
    private readonly TimeSpan _configTtl;
    private readonly Dictionary<string, (object Value, DateTime At)> _configCache = new();

    // GovernedVault field indices (declaration order, 1-based — Odra reserves index 0).
    private const byte IxValueCap = 3, IxBond = 4, IxViolations = 5, IxNextId = 6,
                       IxProposals = 8,
                       IxPaused = 11, IxMaxPerValidator = 12, IxMinActionInterval = 13,
                       IxLastActionTime = 14, IxCommitted = 15;

    public ChainReader(IConfiguration cfg)
    {
        _node = cfg["Casper:NodeRpcUrl"]!;
        _pkg = cfg["Casper:GovernedVaultPackageHash"]!.Replace("hash-", "");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var key = cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) _http.DefaultRequestHeaders.Add("Authorization", key);
        _configTtl = TimeSpan.FromSeconds(cfg.GetValue("Agent:ConfigCacheSeconds", 1800));
    }

    /// Serve a rarely-changing config field from cache for _configTtl. Safe because these values are
    /// either ENFORCED on-chain (cap/per-validator-cap/action-interval — a stale read just risks one
    /// chain-reverted move at worst, never a custody break) or display-only (violations). The
    /// kill-switch (Paused), balances, committed stake, and LastActionTime are NEVER cached here.
    private async Task<T> CachedConfig<T>(string key, Func<Task<T>> read)
    {
        if (_configCache.TryGetValue(key, out var e) && DateTime.UtcNow - e.At < _configTtl)
            return (T)e.Value;
        var v = await read();
        _configCache[key] = (v!, DateTime.UtcNow);
        return v;
    }

    private async Task<JsonElement> Rpc(string method, object prms)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = prms });
        using var resp = await _http.PostAsync(_node, new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) // CSPR.cloud 429s arrive as non-JSON error pages
            throw new ChainRpcException(method, $"HTTP {(int)resp.StatusCode}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { throw new ChainRpcException(method, "non-JSON RPC response"); }
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.GetProperty("message").GetString() ?? "";
                var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                // The node buries the discriminating detail in `data`: an unset dictionary
                // key surfaces as -32003 "Query failed" + data "...value was not found in
                // the global state" (verified live) — message alone can't tell it apart.
                if (err.TryGetProperty("data", out var data)) msg = $"{msg}: {data.ToString()}";
                var notFound = msg.Contains("ValueNotFound", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("Failed to find", StringComparison.OrdinalIgnoreCase);
                throw new ChainRpcException(method, $"{msg} ({code})", notFound);
            }
            return doc.RootElement.GetProperty("result").Clone();
        }
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

    /// Read a field's raw bytes. Returns null ONLY for a genuinely unset field (the node's
    /// value-not-found class); every other failure throws (see ChainRpcException).
    private async Task<byte[]?> ReadBytes(byte index, byte[]? mapKey = null)
    {
        await EnsureResolved();
        var input = mapKey is null ? new byte[] { 0, 0, 0, index } : new byte[] { 0, 0, 0, index }.Concat(mapKey).ToArray();
        var srh = await StateRootHash();
        JsonElement r;
        try
        {
            r = await Rpc("state_get_dictionary_item", new
            {
                state_root_hash = srh,
                dictionary_identifier = new { URef = new { seed_uref = _stateUref, dictionary_item_key = Blake2bHex(input) } }
            });
        }
        catch (ChainRpcException ex) when (ex.NotFound) { return null; } // unset field
        var parsed = r.GetProperty("stored_value").GetProperty("CLValue").GetProperty("parsed");
        if (parsed.ValueKind != JsonValueKind.Array)
            throw new ChainRpcException("state_get_dictionary_item", "unexpected stored-value shape");
        return parsed.EnumerateArray().Select(e => e.GetByte()).ToArray();
    }

    private static byte[] U32Le(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    // Cached (long TTL): owner-set config that changes rarely + is chain-enforced or display-only.
    public Task<decimal> ValueCapCspr() => CachedConfig("cap", async () => OdraBytes.U512Cspr(await ReadBytes(IxValueCap)));
    public Task<decimal> MaxPerValidatorCspr() => CachedConfig("maxpv", async () => OdraBytes.U512Cspr(await ReadBytes(IxMaxPerValidator)));
    public Task<uint> Violations() => CachedConfig("violations", async () => OdraBytes.U32(await ReadBytes(IxViolations)));
    public Task<ulong> ActionIntervalMs() => CachedConfig("interval", async () => OdraBytes.U64(await ReadBytes(IxMinActionInterval)));
    // Fresh every read: kill-switch + values that change on a normal action (no caching).
    public async Task<decimal> BondCspr() => OdraBytes.U512Cspr(await ReadBytes(IxBond));
    public async Task<bool> Paused() => OdraBytes.Bool(await ReadBytes(IxPaused));
    public async Task<uint> NextProposalId() => OdraBytes.U32(await ReadBytes(IxNextId));
    public async Task<ulong> LastActionTimeMs() => OdraBytes.U64(await ReadBytes(IxLastActionTime));
    public async Task<decimal> CommittedCspr(string validatorHex) => OdraBytes.U512Cspr(await ReadBytes(IxCommitted, Convert.FromHexString(validatorHex)));

    /// One on-chain proposal by id, or null if that id was never written.
    public async Task<OdraBytes.ChainProposal?> ProposalById(uint id)
    {
        var b = await ReadBytes(IxProposals, U32Le(id));
        return b is null ? null : OdraBytes.Proposal(b);
    }

    // Resolved proposals are immutable on-chain, so they're cached forever; only open
    // (and brand-new) ids are re-read each tick.
    private readonly Dictionary<uint, OdraBytes.ChainProposal> _resolvedCache = new();

    /// The vault's proposals straight from chain, newest first — the dashboard's
    /// co-sign queue is CHAIN truth, not in-memory bookkeeping (it survives restarts
    /// and can't be forged by replaying old co-sign hashes). Reads at most the most
    /// recent `window` ids to bound RPC fan-out on a long-lived vault.
    public async Task<List<(uint Id, OdraBytes.ChainProposal Proposal)>> Proposals(uint window = 50)
    {
        var n = await NextProposalId();
        var first = n > window ? n - window : 0;
        var list = new List<(uint, OdraBytes.ChainProposal)>();
        for (var id = first; id < n; id++)
        {
            if (_resolvedCache.TryGetValue(id, out var cached)) { list.Add((id, cached)); continue; }
            var p = await ProposalById(id);
            if (p is null) continue;
            if (p.Resolved) _resolvedCache[id] = p;
            list.Add((id, p));
        }
        list.Reverse();
        return list;
    }

    /// Total liquid CSPR in the vault purse (un-delegated; includes the bond).
    public async Task<decimal> TotalBalanceCspr()
    {
        await EnsureResolved();
        var bal = (await Rpc("query_balance", new { purse_identifier = new { purse_uref = _mainPurse } })).GetProperty("balance").GetString()!;
        return decimal.Parse(bal, System.Globalization.CultureInfo.InvariantCulture) / 1_000_000_000m;
    }

    /// Free, deployable CSPR = liquid balance minus the reserved bond.
    public async Task<decimal> FreeBalanceCspr() => await TotalBalanceCspr() - await BondCspr();

    /// Liquid CSPR in an account's main purse (e.g. the agent's gas wallet) — for ops/health.
    public async Task<decimal> AccountBalanceCspr(string publicKeyHex)
    {
        var bal = (await Rpc("query_balance", new { purse_identifier = new { main_purse_under_public_key = publicKeyHex } }))
            .GetProperty("balance").GetString()!;
        return decimal.Parse(bal, System.Globalization.CultureInfo.InvariantCulture) / 1_000_000_000m;
    }
}
