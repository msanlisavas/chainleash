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
    private readonly CasperCloudRestClient _client;
    private readonly bool _mainnet;
    private readonly TimeSpan _ttl;
    private IReadOnlyList<Assessment>? _cache;
    private DateTime _cacheAt;

    public ValidatorMonitor(IConfiguration cfg)
    {
        _cfg = cfg;
        var key = cfg["Casper:CsprCloudAccessKey"] ?? "";
        _client = new CasperCloudRestClient(new CasperCloudClientConfig(key));
        _mainnet = string.Equals(cfg["Casper:ChainName"], "casper", StringComparison.OrdinalIgnoreCase);
        // ~one era (validators don't change intra-era); refreshed lazily on the next Assess.
        _ttl = TimeSpan.FromSeconds(cfg.GetValue("Agent:ValidatorCacheSeconds", 1800));
    }

    /// One validator scored against policy.
    public readonly record struct Assessment(
        string PublicKey, int FeePercent, bool IsActive, decimal StakeCspr, bool Compliant, string Note);

    public string[] Allowlist =>
        _cfg.GetSection("Staking:Allowlist").Get<string[]>() ?? Array.Empty<string>();

    public int MaxCommissionPercent => _cfg.GetValue("Staking:MaxCommissionPercent", 6);

    /// Fetch + score every allowlisted validator (best — lowest compliant fee — first).
    /// Served from cache within the TTL; on a transient CSPR.cloud failure the last-known
    /// set is returned rather than forcing the agent to HOLD on stale-but-fine data.
    public async Task<IReadOnlyList<Assessment>> Assess(CancellationToken ct = default)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAt < _ttl) return _cache;

        var allow = Allowlist;
        if (allow.Length == 0) return Array.Empty<Assessment>();
        var max = MaxCommissionPercent;
        var net = _mainnet ? _client.Mainnet : _client.Testnet;

        Dictionary<string, ValidatorData> byKey;
        try
        {
            var resp = await net.Validator.GetValidatorsAsync(new ValidatorsRequestParameters
            {
                PageSize = Math.Max(allow.Length, 10),
                FilterParameters = new ValidatorsFilterParameters { PublicKeys = allow.ToList() },
            });
            byKey = (resp?.Data ?? new List<ValidatorData>())
                .Where(v => !string.IsNullOrEmpty(v.PublicKey))
                .GroupBy(v => v.PublicKey!.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch when (_cache is not null)
        {
            return _cache; // transient upstream failure — keep serving the last-known set
        }

        var list = new List<Assessment>();
        foreach (var pk in allow)
        {
            if (!byKey.TryGetValue(pk.ToLowerInvariant(), out var v))
            {
                list.Add(new Assessment(pk, -1, false, 0, false, "not in current era set"));
                continue;
            }
            var fee = v.Fee.HasValue ? (int)Math.Round((double)v.Fee.Value) : int.MaxValue; // null fee → fails policy
            var stake = decimal.TryParse(v.TotalStake, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var motes) ? motes / 1_000_000_000m : 0m;
            var compliant = StakingPolicy.IsCompliant(fee, v.IsActive, max);
            var note = StakingPolicy.ComplianceNote(fee, v.IsActive, max);
            list.Add(new Assessment(pk, fee, v.IsActive, stake, compliant, note));
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
}
