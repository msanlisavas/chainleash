using Casper.Network.SDK.Types;

namespace ChainLeash.Agent;

/// The autonomous CHAINLEASH staking agent.
///
/// Each tick it PERCEIVES the allowlisted validators (live CSPR.cloud metrics),
/// scores them against the published delegation policy, and decides whether to
/// act. When it sees an opportunity worth analysing it PAYS over x402 — a real
/// Casper transfer — for a premium risk confirmation ("pay-to-think"); when
/// nothing is actionable it CHOOSES NOT TO ACT, often without paying at all.
///
/// Its on-chain actions are routine delegations/undelegations to the best
/// compliant validator, always within the chain-enforced per-action cap and
/// allowlist. Moves above the cap are pushed on-chain as MATERIAL proposals for
/// the human owner to co-sign — the agent can never execute them alone, and can
/// never withdraw CSPR from the vault. The leash is enforced by the contract,
/// not by this process.
public sealed class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _log;
    private readonly CasperVault _vault;
    private readonly X402Client _x402;
    private readonly ValidatorMonitor _validators;
    private readonly IConfiguration _cfg;
    private readonly IHostApplicationLifetime _life;

    // In-memory view of what the agent has deployed (it is the sole delegator).
    private readonly Dictionary<string, decimal> _delegated = new();
    private decimal _idleCspr;
    private int _tick, _actions, _buys;

    public AgentWorker(ILogger<AgentWorker> log, CasperVault vault, X402Client x402, ValidatorMonitor validators, IConfiguration cfg, IHostApplicationLifetime life)
    {
        _log = log; _vault = vault; _x402 = x402; _validators = validators; _cfg = cfg; _life = life;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(_cfg.GetValue("Agent:TickSeconds", 20));
        var cap = _cfg.GetValue("Staking:PerActionCapCspr", 600m);
        var chunk = _cfg.GetValue("Staking:DeployChunkCspr", 500m);
        var maxActions = _cfg.GetValue("Agent:MaxOnChainActions", 3);
        var maxBuys = _cfg.GetValue("Agent:MaxSignalBuys", 4);
        var maxTicks = _cfg.GetValue("Agent:MaxTicks", 0); // 0 = run forever
        _idleCspr = _cfg.GetValue("Staking:TreasuryToDeployCspr", 0m);

        _log.LogInformation("CHAINLEASH staking agent online — cap={Cap} CSPR/action, tick={S}s, idle treasury={Idle} CSPR, policy: commission ≤ {Max}%",
            cap, period.TotalSeconds, _idleCspr, _validators.MaxCommissionPercent);

        while (!ct.IsCancellationRequested)
        {
            _tick++;
            try { await Tick(cap, chunk, maxActions, maxBuys, ct); }
            catch (Exception ex) { _log.LogError(ex, "tick {T} error", _tick); }

            if (maxTicks > 0 && _tick >= maxTicks)
            {
                _log.LogInformation("reached MaxTicks={N} — {A} on-chain action(s), {B} signal buy(s). Stopping.", maxTicks, _actions, _buys);
                _life.StopApplication();
                break;
            }
            try { await Task.Delay(period, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private async Task Tick(decimal cap, decimal chunk, int maxActions, int maxBuys, CancellationToken ct)
    {
        var assessments = await _validators.Assess(ct);
        if (assessments.Count == 0) { _log.LogInformation("tick {T}: no allowlisted validators configured.", _tick); return; }

        // 1) Is anything delegated now breaching policy? (e.g. commission hiked, evicted)
        var breach = assessments.FirstOrDefault(a => !a.Compliant && _delegated.GetValueOrDefault(a.PublicKey) > 0);
        // 2) Best compliant validator to deploy idle treasury into.
        var best = assessments.FirstOrDefault(a => a.Compliant);

        var hasBreach = breach.PublicKey is not null && !breach.Compliant && _delegated.GetValueOrDefault(breach.PublicKey) > 0;
        var canDeploy = _idleCspr >= chunk && best.PublicKey is not null && best.Compliant;

        if (!hasBreach && !canDeploy)
        {
            var seen = best.PublicKey is not null
                ? $"best allowlisted = {best.PublicKey[..12]}… ({best.Note})"
                : "no compliant validator in policy";
            _log.LogInformation("tick {T}: perceived {N} validators, {Seen}; treasury deployed & nothing off-policy → CHOSE NOT TO ACT (no spend).",
                _tick, assessments.Count, seen);
            return;
        }
        if (_actions >= maxActions)
        {
            _log.LogInformation("tick {T}: on-chain action budget spent ({N}) → holding.", _tick, _actions);
            return;
        }

        // Pay-to-think: a candidate action exists, so it's worth buying the premium risk read.
        var escalate = false;
        if (_buys < maxBuys)
        {
            try
            {
                _buys++;
                var sig = await _x402.BuySignal();
                var hash = string.IsNullOrEmpty(sig.SettlementHash) ? "(open)" : sig.SettlementHash[..8];
                _log.LogInformation("tick {T}: PAID {Paid:F2} CSPR over x402 ({Hash}) for the premium risk read → risk {Risk}",
                    _tick, sig.PaidMotes / 1e9, hash, sig.Risk);
                escalate = sig.Risk == "elevated";
                if (escalate) _log.LogInformation("  premium read flags ELEVATED risk → escalating this move to human co-sign.");
            }
            catch (Exception ex) { _log.LogWarning("  x402 read unavailable ({Msg}) — proceeding on the free policy signal.", ex.Message); }
        }

        // A policy breach is urgent — exit fast (routine, ≤ cap). A fresh deploy on an
        // elevated risk read is escalated to the human even when it's within the cap.
        if (hasBreach) await ExitBreach(breach, cap, ct);
        else await Deploy(best, chunk, cap, escalate, ct);
    }

    /// A delegated validator broke policy → reduce/exit the position (rebalance away).
    private async Task ExitBreach(ValidatorMonitor.Assessment breach, decimal cap, CancellationToken ct)
    {
        var position = _delegated.GetValueOrDefault(breach.PublicKey);
        var amount = Math.Min(position, cap);
        _log.LogWarning("tick {T}: POLICY BREACH on {V}… ({Note}) — undelegating {Amt} CSPR back to the vault.",
            _tick, breach.PublicKey[..12], breach.Note, amount);

        var validator = PublicKey.FromHexString(breach.PublicKey);
        if (position > cap)
        {
            await Propose(validator, position, undelegate: true, $"exit breaching validator (position {position} > cap {cap})");
            return;
        }
        var r = await _vault.Undelegate(validator, Motes(amount));
        Audit("UNDELEGATE", breach.PublicKey, amount, r);
        if (r.Success) { _delegated[breach.PublicKey] = position - amount; _idleCspr += amount; _actions++; }
    }

    /// Idle treasury + a compliant best validator → deploy a chunk into it.
    private async Task Deploy(ValidatorMonitor.Assessment best, decimal chunk, decimal cap, bool escalate, CancellationToken ct)
    {
        var amount = Math.Min(_idleCspr, chunk);
        var validator = PublicKey.FromHexString(best.PublicKey);

        if (amount > cap || escalate)
        {
            var why = amount > cap ? $"deploy {amount} > cap {cap}" : "elevated risk read → human co-sign";
            await Propose(validator, amount, undelegate: false, why);
            return;
        }
        _log.LogInformation("tick {T}: deploying {Amt} CSPR to best validator {V}… ({Note}) — routine, ≤ cap.",
            _tick, amount, best.PublicKey[..12], best.Note);
        var r = await _vault.Delegate(validator, Motes(amount));
        Audit("DELEGATE", best.PublicKey, amount, r);
        if (r.Success) { _delegated[best.PublicKey] = _delegated.GetValueOrDefault(best.PublicKey) + amount; _idleCspr -= amount; _actions++; }
    }

    private async Task Propose(PublicKey validator, decimal amount, bool undelegate, string why)
    {
        _log.LogInformation("tick {T}: MATERIAL move ({Why}) — proposing on-chain for the human owner to co-sign…", _tick, why);
        var r = await _vault.ProposeMaterial(validator, Motes(amount), undelegate);
        Audit(undelegate ? "PROPOSE-UNDELEGATE" : "PROPOSE-DELEGATE", validator.ToAccountHex(), amount, r);
        if (r.Success) _actions++;
    }

    private void Audit(string action, string validator, decimal amountCspr, TxResult r)
    {
        if (r.Success) _log.LogInformation("  ✓ {Action} {Amt} CSPR → {V}…  on-chain: {Url}", action, amountCspr, validator[..12], r.Url);
        else _log.LogWarning("  ✗ {Action} rejected by the leash: {Err}  ({Url})", action, r.Error, r.Url);
    }

    private static ulong Motes(decimal cspr) => (ulong)(cspr * 1_000_000_000m);
}
