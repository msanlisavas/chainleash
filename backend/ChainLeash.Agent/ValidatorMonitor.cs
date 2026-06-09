using System.Text.Json;

namespace ChainLeash.Agent;

/// The agent's PERCEPTION layer: pulls live validator metrics (commission, active
/// status, stake) from CSPR.cloud and scores the allowlisted set against the
/// published delegation policy (max commission, must-be-active). This is real,
/// free, on-chain-sourced data — Casper's actual staking primitive, not a mock.
///
/// The policy is deterministic and transparent on purpose: institutions want the
/// agent to EXECUTE a published rule auditably, not to exercise opaque discretion.
public sealed class ValidatorMonitor
{
    private readonly IConfiguration _cfg;
    private readonly HttpClient _http;

    public ValidatorMonitor(IConfiguration cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { BaseAddress = new Uri(cfg["Casper:CsprCloudBaseUrl"]!.TrimEnd('/') + "/") };
        var key = cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) _http.DefaultRequestHeaders.Add("Authorization", key);
    }

    /// One validator scored against policy.
    public readonly record struct Assessment(
        string PublicKey, int FeePercent, bool IsActive, decimal StakeCspr, bool Compliant, string Note);

    public string[] Allowlist =>
        _cfg.GetSection("Staking:Allowlist").Get<string[]>() ?? Array.Empty<string>();

    public int MaxCommissionPercent => _cfg.GetValue("Staking:MaxCommissionPercent", 6);

    /// Fetch + score every allowlisted validator, best (lowest compliant fee) first.
    public async Task<IReadOnlyList<Assessment>> Assess(CancellationToken ct = default)
    {
        var byKey = await FetchEraValidators(ct);
        var max = MaxCommissionPercent;

        var list = new List<Assessment>();
        foreach (var pk in Allowlist)
        {
            if (!byKey.TryGetValue(pk.ToLowerInvariant(), out var m))
            {
                list.Add(new Assessment(pk, -1, false, 0, false, "not in current era set"));
                continue;
            }
            var compliant = StakingPolicy.IsCompliant(m.FeePercent, m.IsActive, max);
            var note = StakingPolicy.ComplianceNote(m.FeePercent, m.IsActive, max);
            list.Add(new Assessment(pk, m.FeePercent, m.IsActive, m.StakeCspr, compliant, note));
        }

        // Best first: compliant before breaching, then lowest commission, then more stake (stability).
        return list
            .OrderByDescending(a => a.Compliant)
            .ThenBy(a => a.FeePercent < 0 ? int.MaxValue : a.FeePercent)
            .ThenByDescending(a => a.StakeCspr)
            .ToList();
    }

    private readonly record struct Metric(int FeePercent, bool IsActive, decimal StakeCspr);

    private async Task<Dictionary<string, Metric>> FetchEraValidators(CancellationToken ct)
    {
        var era = await CurrentEra(ct);
        var map = new Dictionary<string, Metric>(StringComparer.OrdinalIgnoreCase);
        // Paginate: the era set can exceed one page, and a missed page would misclassify a
        // healthy allowlisted validator as "not in current era set" — and actively exit it.
        for (var page = 1; page <= 10; page++)
        {
            using var doc = await GetJson($"validators?era_id={era}&page={page}&page_size=100", ct);
            foreach (var v in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var pk = v.GetProperty("public_key").GetString()!.ToLowerInvariant();
                var fee = v.TryGetProperty("fee", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
                var active = v.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True;
                var stake = 0m;
                if (v.TryGetProperty("total_stake", out var s) && s.ValueKind == JsonValueKind.String
                    && decimal.TryParse(s.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var motes))
                    stake = motes / 1_000_000_000m;
                map[pk] = new Metric(fee, active, stake);
            }
            var pageCount = doc.RootElement.TryGetProperty("page_count", out var pc) && pc.ValueKind == JsonValueKind.Number
                ? pc.GetInt32() : 1;
            if (page >= pageCount) break;
        }
        return map;
    }

    private async Task<long> CurrentEra(CancellationToken ct)
    {
        using var doc = await GetJson("auction-metrics", ct);
        return doc.RootElement.GetProperty("data").GetProperty("current_era_id").GetInt64();
    }

    /// CSPR.cloud REST fetch with the failure CLASS made readable: a 429/quota response
    /// arrives as a non-JSON body, and "'d' is an invalid start of a value" in the audit
    /// feed helps nobody — name the real condition instead.
    private async Task<JsonDocument> GetJson(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"CSPR.cloud {path}: HTTP {(int)resp.StatusCode} (rate limit / quota?)");
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new HttpRequestException($"CSPR.cloud {path}: non-JSON response (rate limit / quota?)"); }
    }
}
