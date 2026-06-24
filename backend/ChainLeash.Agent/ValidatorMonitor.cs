using CSPR.Cloud.Net.Clients;
using CSPR.Cloud.Net.Objects.Config;
using CSPR.Cloud.Net.Objects.Validator;
using CSPR.Cloud.Net.Parameters.Filtering.Validator;
using CSPR.Cloud.Net.Parameters.Wrapper.Validator;

namespace ChainLeash.Agent;

/// The agent's PERCEPTION layer: pulls live metrics (commission, active status, stake) for
/// ONLY the allowlisted validators in a SINGLE filtered CSPR.cloud request (CSPR.Cloud.Net
/// SDK, public_key filter) and scores them against the published delegation policy.
///
/// The validator set changes per era (~2h on Casper 2.0), not per tick — so the result is
/// cached for ~an era and refreshed lazily. This replaced a per-tick 10-page scan of the
/// whole validator set; request volume is now ~1 call per cache window instead of ~11/tick.
///
/// The policy is deterministic and transparent on purpose: institutions want the agent to
/// EXECUTE a published rule auditably, not to exercise opaque discretion.
public sealed class ValidatorMonitor
{
    private readonly IConfiguration _cfg;
    private readonly ChainReader _chain;
    private readonly CasperCloudRestClient _client;
    private readonly bool _mainnet;
    private readonly TimeSpan _ttl;
    private IReadOnlyList<Assessment>? _cache;
    private DateTime _cacheAt;
    // Validator branding (account-info names) — fetched via raw REST (the SDK query above doesn't
    // surface it) and cached per era alongside the policy assessment.
    private readonly HttpClient _http;
    private readonly string _restBase;
    private Dictionary<string, string>? _names;
    private DateTime _namesAt;

    public ValidatorMonitor(IConfiguration cfg, ChainReader chain)
    {
        _cfg = cfg;
        _chain = chain;
        var key = cfg["Casper:CsprCloudAccessKey"] ?? "";
        _client = new CasperCloudRestClient(new CasperCloudClientConfig(key));
        _mainnet = string.Equals(cfg["Casper:ChainName"], "casper", StringComparison.OrdinalIgnoreCase);
        // ~one era (validators don't change intra-era); refreshed lazily on the next Assess.
        _ttl = TimeSpan.FromSeconds(cfg.GetValue("Agent:ValidatorCacheSeconds", 1800));
        _restBase = (cfg["Casper:CsprCloudBaseUrl"] ?? "https://api.testnet.cspr.cloud").TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(key)) _http.DefaultRequestHeaders.Add("Authorization", key);
    }

    /// One validator scored against policy. `Name` is the validator's registered account-info
    /// branding (null when it hasn't registered one). `Allowed` is the on-chain allowlist status
    /// (owner-controlled) — a removed validator is non-compliant regardless of its metrics.
    public readonly record struct Assessment(
        string PublicKey, int FeePercent, bool IsActive, decimal StakeCspr, bool Compliant, string Note, string? Name = null, bool Allowed = true);

    public string[] Allowlist =>
        _cfg.GetSection("Staking:Allowlist").Get<string[]>() ?? Array.Empty<string>();

    public int MaxCommissionPercent => _cfg.GetValue("Staking:MaxCommissionPercent", 6);

    /// Score every config-allowlisted validator (best — lowest compliant fee — first), then apply
    /// the LIVE on-chain allowlist. Metrics (commission/active/stake/name) are cached per era; the
    /// on-chain allowlist is read FRESH each call so an owner allow/disallow takes effect next tick.
    public async Task<IReadOnlyList<Assessment>> Assess(CancellationToken ct = default)
    {
        var metrics = await Metrics(ct);
        if (metrics.Count == 0) return metrics;
        var adjusted = new List<Assessment>(metrics.Count);
        foreach (var m in metrics)
        {
            // IsValidatorAllowed is fail-safe (true on any read error/unset) — a hiccup can never
            // make the agent treat its own validators as off-policy and churn their stake.
            var allowed = await _chain.IsValidatorAllowed(m.PublicKey);
            adjusted.Add(allowed
                ? m with { Allowed = true }
                : m with { Allowed = false, Compliant = false, Note = "removed from the allowlist by the owner" });
        }
        // Re-order with the allowlist applied: compliant first, then lowest fee, then more stake.
        return adjusted
            .OrderByDescending(a => a.Compliant)
            .ThenBy(a => a.FeePercent < 0 ? int.MaxValue : a.FeePercent)
            .ThenByDescending(a => a.StakeCspr)
            .ToList();
    }

    /// Live CSPR.cloud metrics + commission/active verdict per validator (the on-chain allowlist is
    /// layered on in Assess). Served from cache within the TTL; a transient CSPR.cloud failure
    /// returns the last-known set rather than forcing the agent to HOLD on stale-but-fine data.
    private async Task<IReadOnlyList<Assessment>> Metrics(CancellationToken ct)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAt < _ttl) return _cache;

        var allow = Allowlist;
        if (allow.Length == 0) return Array.Empty<Assessment>();
        var max = MaxCommissionPercent;
        var net = _mainnet ? _client.Mainnet : _client.Testnet;

        Dictionary<string, ValidatorData> byKey;
        try
        {
            // The CSPR.cloud validators endpoint requires an era_id — fetch the current era first
            // (one extra REST call per cache window, on the healthy REST quota).
            var metrics = await net.Auction.GetAuctionMetricsAsync();
            var era = metrics?.Data?.CurrentEraId
                ?? throw new InvalidOperationException("CSPR.cloud returned no current_era_id");
            var resp = await net.Validator.GetValidatorsAsync(new ValidatorsRequestParameters
            {
                PageSize = Math.Max(allow.Length, 10),
                FilterParameters = new ValidatorsFilterParameters
                {
                    PublicKeys = allow.ToList(),
                    EraId = era.ToString(),
                },
            });
            byKey = (resp?.Data ?? new List<ValidatorData>())
                .Where(v => !string.IsNullOrEmpty(v.PublicKey))
                .GroupBy(v => v.PublicKey!.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());
            await EnsureNames(era.ToString(), ct); // validator branding (account-info names), cached
        }
        catch when (_cache is not null)
        {
            return _cache; // transient upstream failure — keep serving the last-known set
        }

        var names = _names ?? new Dictionary<string, string>();
        var list = new List<Assessment>();
        foreach (var pk in allow)
        {
            var name = names.GetValueOrDefault(pk.ToLowerInvariant());
            if (!byKey.TryGetValue(pk.ToLowerInvariant(), out var v))
            {
                list.Add(new Assessment(pk, -1, false, 0, false, "not in current era set", name));
                continue;
            }
            var fee = v.Fee.HasValue ? (int)Math.Round((double)v.Fee.Value) : int.MaxValue; // null fee → fails policy
            var stake = decimal.TryParse(v.TotalStake, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var motes) ? motes / 1_000_000_000m : 0m;
            var compliant = StakingPolicy.IsCompliant(fee, v.IsActive, max);
            var note = StakingPolicy.ComplianceNote(fee, v.IsActive, max);
            list.Add(new Assessment(pk, fee, v.IsActive, stake, compliant, note, name));
        }

        // Best first: compliant before breaching, then lowest commission, then more stake.
        var ordered = list
            .OrderByDescending(a => a.Compliant)
            .ThenBy(a => a.FeePercent < 0 ? int.MaxValue : a.FeePercent)
            .ThenByDescending(a => a.StakeCspr)
            .ToList();
        _cache = ordered;
        _cacheAt = DateTime.UtcNow;
        return ordered;
    }

    /// Refresh the validator-name map (account-info branding) for the current era, cached for the
    /// same window as the assessment. Names are a display nicety — a fetch failure is non-fatal
    /// (keep the last-known map, or none), it must never block the policy assessment.
    private async Task EnsureNames(string era, CancellationToken ct)
    {
        if (_names is not null && DateTime.UtcNow - _namesAt < _ttl) return;
        try
        {
            using var resp = await _http.GetAsync($"{_restBase}/validators?era_id={era}&page_size=100&includes=account_info", ct);
            resp.EnsureSuccessStatusCode();
            _names = ValidatorNames.Parse(await resp.Content.ReadAsStringAsync(ct));
            _namesAt = DateTime.UtcNow;
        }
        catch { /* names optional — keep whatever we had (possibly none) */ }
    }
}
