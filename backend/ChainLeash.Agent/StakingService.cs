namespace ChainLeash.Agent;

/// One delegated position as the dashboard renders it.
public sealed record PositionView(
    string PublicKey, int FeePercent, bool Active, bool Compliant,
    decimal PrincipalCspr, decimal CurrentStakeCspr, decimal RewardCspr, string Status, string? Name = null);

/// The vault's full staking picture: positions + portfolio totals.
public sealed record StakingView(
    IReadOnlyList<PositionView> Positions,
    decimal TotalPrincipalCspr, decimal TotalCurrentStakeCspr, decimal TotalRewardCspr,
    decimal FreeBalanceCspr, decimal BondCspr, decimal TotalUnderManagementCspr, bool Stale);

/// Builds "where the vault delegated + how much it earned" for the dashboard. cspr.live can't
/// show this (the vault is a uref delegator); CSPR.cloud's REST index can. Per-validator current
/// stake comes from CSPR.cloud; principal from the contract's `committed`; reward = current −
/// principal (auto-compounded). Cached ~per era; one concurrent compute (coalesced).
public sealed class StakingService
{
    private readonly ValidatorMonitor _validators;
    private readonly ChainReader _chain;
    private readonly HttpClient _http;
    private readonly string _restBase;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StakingView? _cache;
    private DateTime _cacheAt;

    public StakingService(IConfiguration cfg, ValidatorMonitor validators, ChainReader chain)
    {
        _validators = validators; _chain = chain;
        _restBase = (cfg["Casper:CsprCloudBaseUrl"] ?? "https://api.testnet.cspr.cloud").TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var key = cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) _http.DefaultRequestHeaders.Add("Authorization", key);
        _ttl = TimeSpan.FromSeconds(cfg.GetValue("Agent:ValidatorCacheSeconds", 1800));
    }

    public async Task<StakingView> GetAsync(CancellationToken ct = default)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAt < _ttl) return _cache;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTime.UtcNow - _cacheAt < _ttl) return _cache; // someone else just filled it
            var view = await Build(ct);
            _cache = view; _cacheAt = DateTime.UtcNow;
            return view;
        }
        finally { _gate.Release(); }
    }

    private async Task<StakingView> Build(CancellationToken ct)
    {
        var assessments = await _validators.Assess(ct);
        var urefAddr = StakingPositions.UrefAddress(await _chain.VaultPurseUref());
        var bond = await _chain.BondCspr();
        var total = await _chain.TotalBalanceCspr();
        var free = total - bond;

        // validators with an unresolved owner-exit proposal → label distinctly (don't read as "earning")
        var exiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (_, p) in await _chain.Proposals())
                if (!p.Resolved && p.Undelegate) exiting.Add(p.ValidatorHex);
        }
        catch { /* non-fatal: status falls back to delegated/unbonding */ }

        var positions = new List<PositionView>();
        var stale = false;
        foreach (var a in assessments)
        {
            var principal = await _chain.CommittedCspr(a.PublicKey);
            decimal current;
            try { current = await VaultStakeOn(a.PublicKey, urefAddr, ct); }
            catch { stale = true; current = principal; } // CSPR.cloud hiccup → fall back to principal, flag stale
            if (current <= 0m && principal <= 0m) continue; // skip allowlisted-but-unused validators
            var reward = RewardMath.RewardCspr(current, principal);
            var status =
                exiting.Contains(a.PublicKey) ? "Exit proposed — awaiting owner co-sign"
                : principal <= 0m && current > 0m ? "Unbonding"
                : "Delegated";
            positions.Add(new PositionView(a.PublicKey, a.FeePercent, a.IsActive, a.Compliant,
                principal, current, reward, status, a.Name));
        }

        var staked = positions.Sum(p => p.CurrentStakeCspr);
        return new StakingView(positions,
            positions.Sum(p => p.PrincipalCspr), staked, positions.Sum(p => p.RewardCspr),
            free, bond, staked + free + bond, stale);
    }

    private async Task<decimal> VaultStakeOn(string validatorHex, string urefAddr, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{_restBase}/validators/{validatorHex}/delegations?page_size=100", ct);
        resp.EnsureSuccessStatusCode();
        return StakingPositions.VaultStakeCspr(await resp.Content.ReadAsStringAsync(ct), urefAddr);
    }
}
