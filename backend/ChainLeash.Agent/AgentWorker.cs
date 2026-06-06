namespace ChainLeash.Agent;

/// The autonomous agent loop. Each tick it perceives a rate/risk signal, runs
/// explicit expected-value math to decide whether acting is even worth it
/// (and frequently CHOOSES NOT TO ACT), and when a move is material (over the
/// chain-enforced cap) it proposes it on-chain for the human to co-sign.
///
/// The EV policy here is deterministic + transparent; the LLM (Claude) decision
/// layer plugs in at MakeDecision() next. The signal is a local mock until the
/// x402 consumer lands.
public sealed class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _log;
    private readonly CasperVault _vault;
    private readonly IConfiguration _cfg;
    private readonly Random _rng = new();
    private int _tick;
    private int _proposals;

    public AgentWorker(ILogger<AgentWorker> log, CasperVault vault, IConfiguration cfg)
    {
        _log = log; _vault = vault; _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cap = _cfg.GetValue("Agent:ValueCapCspr", 2.0m);
        var signalCost = _cfg.GetValue("Agent:SignalCostCspr", 0.4m);
        var period = TimeSpan.FromSeconds(_cfg.GetValue("Agent:TickSeconds", 20));
        var maxProposals = _cfg.GetValue("Agent:MaxOnChainProposals", 3);
        var band = 4.5m;

        _log.LogInformation("CHAINLEASH agent online — cap={Cap} CSPR, signal={Cost} CSPR, tick={S}s", cap, signalCost, period.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            _tick++;
            // Perceive: a mock rate/risk signal (~3.5–5.5%). Real x402 fetch is next.
            var rate = 4.5m + (decimal)(_rng.NextDouble() * 2.0 - 1.0);
            var edge = Math.Max(0m, rate - band);          // expected edge of reallocating
            var move = 10m * Math.Max(0m, rate - band);    // size of the candidate move

            if (edge <= signalCost)
            {
                _log.LogInformation("tick {T}: rate {Rate:F2}% → edge {Edge:F2} ≤ signal {Cost} CSPR. CHOSE NOT TO ACT (didn't even buy the signal).",
                    _tick, rate, edge, signalCost);
            }
            else if (move <= cap)
            {
                _log.LogInformation("tick {T}: rate {Rate:F2}% → edge {Edge:F2} CSPR. ROUTINE move {Move:F2} CSPR (≤ cap) — would settle autonomously. [settle lands once treasury is funded]",
                    _tick, rate, edge, move);
            }
            else
            {
                _log.LogInformation("tick {T}: rate {Rate:F2}% → edge {Edge:F2} CSPR. MATERIAL move {Move:F2} CSPR (> cap {Cap}) — proposing on-chain for human co-sign…",
                    _tick, rate, edge, move, cap);
                if (_proposals < maxProposals)
                {
                    _proposals++;
                    try
                    {
                        var r = await _vault.ProposeMaterial(_vault.AgentKey, (ulong)(move * 1_000_000_000m));
                        if (r.Success) _log.LogInformation("  ✓ MaterialProposed on-chain: {Url}", r.Url);
                        else _log.LogWarning("  propose failed: {Err} ({Hash})", r.Error, r.Hash);
                    }
                    catch (Exception ex) { _log.LogError(ex, "  propose_material error"); }
                }
                else
                {
                    _log.LogInformation("  (on-chain proposal cap reached — logging only)");
                }
            }

            try { await Task.Delay(period, ct); } catch (TaskCanceledException) { break; }
        }
    }
}
