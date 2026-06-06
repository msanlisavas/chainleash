namespace ChainLeash.Agent;

/// The autonomous agent loop with x402 "pay-to-think":
/// 1. a free/cheap prior decides whether the data is even worth buying;
/// 2. if so, it PAYS over x402 (a real Casper transfer) for the premium signal;
/// 3. it runs expected-value math and, when a move is material (over the
///    chain-enforced cap), proposes it on-chain for the human to co-sign.
/// It frequently CHOOSES NOT TO ACT — often without even buying the signal.
///
/// EV policy is deterministic + transparent; the Claude decision layer plugs in
/// at the marked seam once an Anthropic key is configured.
public sealed class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _log;
    private readonly CasperVault _vault;
    private readonly X402Client _x402;
    private readonly IConfiguration _cfg;
    private readonly Random _rng = new();
    private int _tick, _proposals, _buys;

    public AgentWorker(ILogger<AgentWorker> log, CasperVault vault, X402Client x402, IConfiguration cfg)
    {
        _log = log; _vault = vault; _x402 = x402; _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cap = _cfg.GetValue("Agent:ValueCapCspr", 2.0m);
        var period = TimeSpan.FromSeconds(_cfg.GetValue("Agent:TickSeconds", 20));
        var maxProposals = _cfg.GetValue("Agent:MaxOnChainProposals", 3);
        var maxBuys = _cfg.GetValue("Agent:MaxSignalBuys", 3);
        var band = 4.7m;

        _log.LogInformation("CHAINLEASH agent online — cap={Cap} CSPR, tick={S}s", cap, period.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            _tick++;
            var priorInteresting = _rng.NextDouble() < 0.45; // free prior: is anything happening?

            if (!priorInteresting)
            {
                _log.LogInformation("tick {T}: prior quiet → CHOSE NOT TO ACT (didn't even buy the signal).", _tick);
            }
            else if (_buys >= maxBuys)
            {
                _log.LogInformation("tick {T}: prior interesting but signal-buy budget spent → holding.", _tick);
            }
            else
            {
                try
                {
                    _buys++;
                    var sig = await _x402.BuySignal();
                    var hash = string.IsNullOrEmpty(sig.SettlementHash) ? "(open)" : sig.SettlementHash[..8];
                    _log.LogInformation("tick {T}: PAID {Paid:F2} CSPR for the premium signal over x402 ({Hash}) → rate {Rate:F2}% risk {Risk}",
                        _tick, sig.PaidMotes / 1e9, hash, sig.Rate, sig.Risk);

                    var edge = Math.Max(0m, (decimal)sig.Rate - band);
                    var move = 12m * edge;
                    if (edge <= 0m)
                        _log.LogInformation("  signal below band → no edge. Hold (paid to learn it's not worth acting).");
                    else if (move <= cap)
                        _log.LogInformation("  ROUTINE move {Move:F2} CSPR (≤ cap) — would settle autonomously. [settle lands once treasury funded]", move);
                    else
                    {
                        _log.LogInformation("  MATERIAL move {Move:F2} CSPR (> cap {Cap}) — proposing on-chain for human co-sign…", move, cap);
                        if (_proposals < maxProposals)
                        {
                            _proposals++;
                            var r = await _vault.ProposeMaterial(_vault.AgentKey, (ulong)(move * 1_000_000_000m));
                            if (r.Success) _log.LogInformation("    ✓ MaterialProposed on-chain: {Url}", r.Url);
                            else _log.LogWarning("    propose failed: {Err}", r.Error);
                        }
                    }
                }
                catch (Exception ex) { _log.LogError(ex, "tick {T}: signal/act error", _tick); }
            }

            try { await Task.Delay(period, ct); } catch (TaskCanceledException) { break; }
        }
    }
}
