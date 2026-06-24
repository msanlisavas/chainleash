using System.Text.Json;

namespace ChainLeash.Agent;

/// One validator in the pick-list (public key + registered branding name + commission + active).
public sealed record DirectoryValidator(string PublicKey, string? Name, int FeePercent, bool Active);

/// The full directory of testnet validators for the dashboard's "add validator" search — so the
/// owner can pick by name/key instead of copy-pasting. Fetched from CSPR.cloud, cached ~per era;
/// one concurrent fetch (coalesced).
public sealed class ValidatorDirectory
{
    private readonly HttpClient _http;
    private readonly string _restBase;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DirectoryValidator>? _cache;
    private DateTime _cacheAt;

    public ValidatorDirectory(IConfiguration cfg)
    {
        _restBase = (cfg["Casper:CsprCloudBaseUrl"] ?? "https://api.testnet.cspr.cloud").TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var key = cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) _http.DefaultRequestHeaders.Add("Authorization", key);
        _ttl = TimeSpan.FromSeconds(cfg.GetValue("Agent:ValidatorCacheSeconds", 1800));
    }

    public async Task<IReadOnlyList<DirectoryValidator>> ListAsync(CancellationToken ct = default)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAt < _ttl) return _cache;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTime.UtcNow - _cacheAt < _ttl) return _cache;
            using var m = await _http.GetAsync($"{_restBase}/auction-metrics", ct);
            m.EnsureSuccessStatusCode();
            using var mDoc = JsonDocument.Parse(await m.Content.ReadAsStringAsync(ct));
            var era = mDoc.RootElement.GetProperty("data").GetProperty("current_era_id").GetInt64();
            using var resp = await _http.GetAsync(
                $"{_restBase}/validators?era_id={era}&page_size=100&includes=account_info&order_by=total_stake&order_direction=desc", ct);
            resp.EnsureSuccessStatusCode();
            var list = Parse(await resp.Content.ReadAsStringAsync(ct));
            _cache = list; _cacheAt = DateTime.UtcNow;
            return list;
        }
        finally { _gate.Release(); }
    }

    /// Pure parse of CSPR.cloud's validators (with includes=account_info) into the pick-list.
    public static IReadOnlyList<DirectoryValidator> Parse(string json)
    {
        var result = new List<DirectoryValidator>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var v in data.EnumerateArray())
        {
            var pk = v.TryGetProperty("public_key", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(pk)) continue;
            var fee = v.TryGetProperty("fee", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
            var active = v.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True;
            string? name = null;
            if (v.TryGetProperty("account_info", out var ai) && ai.ValueKind == JsonValueKind.Object
                && ai.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
                && owner.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                name = nm.GetString();
            result.Add(new DirectoryValidator(pk.ToLowerInvariant(), string.IsNullOrWhiteSpace(name) ? null : name!.Trim(), fee, active));
        }
        return result;
    }
}
