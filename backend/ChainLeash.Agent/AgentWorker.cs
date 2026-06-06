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
/// not by this process. Every decision is streamed to the dashboard audit feed.
public sealed class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _log;
    private readonly CasperVault _vault;
    private readonly X402Client _x402;
    private readonly ValidatorMonitor _validators;
    private readonly IConfiguration _cfg;
    private readonly IHostApplicationLifetime _life;
    private readonly AuditFeed _feed;

    // In-memory view of what the agent has deployed (it is the sole delegator).
    private readonly Dictionary<string, decimal> _delegated = new();
    private decimal _idleCspr;
    private int _tick, _actions, _buys;
    private uint _nextProposalId;

    public AgentWorker(ILogger<AgentWorker> log, CasperVault vault, X402Client x402, ValidatorMonitor validators, IConfiguration cfg, IHostApplicationLifetime life, AuditFeed feed)
    {
        _log = log; _vault = vault; _x402 = x402; _validators = validators; _cfg = cfg; _life = life; _feed = feed;
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
        _nextProposalId = (uint)_cfg.GetValue("Staking:NextProposalId", 0);

        _feed.State.PackageHash = (_cfg["Casper:GovernedVaultPackageHash"] ?? "").Replace("hash-", "");
        _feed.State.CapCspr = cap;
        _feed.State.MaxCommissionPercent = _validators.MaxCommissionPercent;

        _log.LogInformation("CHAINLEASH staking agent online — cap={Cap} CSPR/action, tick={S}s, idle treasury={Idle} CSPR, policy: commission ≤ {Max}%",
            cap, period.TotalSeconds, _idleCspr, _validators.MaxCommissionPercent);
        await Emit("ONLINE", $"Agent online — cap {cap} CSPR/action, policy: commission ≤ {_validators.MaxCommissionPercent}%, idle treasury {_idleCspr} CSPR.");

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
        await RefreshState(assessments);

        // 1) Is anything delegated now breaching policy? (e.g. commission hiked, evicted)
        var breach = assessments.FirstOrDefault(a => !a.Compliant && _delegated.GetValueOrDefault(a.PublicKey) > 0);
        // 2) Best compliant validator to deploy idle treasury into.
        var best = assessments.FirstOrDefault(a => a.Compliant);

        var hasBreach = breach.PublicKey is not null && !breach.Compliant && _delegated.GetValueOrDefault(breach.PublicKey) > 0;
        var canDeploy = _idleCspr >= chunk && best.PublicKey is not null && best.Compliant;

        if (!hasBreach && !canDeploy)
        {
            var seen = best.PublicKey is not null ? $"best allowlisted {best.PublicKey[..10]}… ({best.Note})" : "no compliant validator";
            _log.LogInformation("tick {T}: perceived {N} validators, {Seen}; nothing off-policy → CHOSE NOT TO ACT (no spend).", _tick, assessments.Count, seen);
            await Emit("HOLD", $"Perceived {assessments.Count} validators, {seen}; treasury deployed & nothing off-policy → chose not to act (no spend).");
            return;
        }
        if (_actions >= maxActions)
        {
            _log.LogInformation("tick {T}: on-chain action budget spent ({N}) → holding.", _tick, _actions);
            await Emit("HOLD", $"On-chain action budget spent ({_actions}) → holding.");
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
                _feed.State.Buys = _buys;
                _feed.State.X402SpentCspr += (decimal)sig.PaidMotes / 1_000_000_000m;
                var hash = string.IsNullOrEmpty(sig.SettlementHash) ? null : sig.SettlementHash;
                _log.LogInformation("tick {T}: PAID {Paid:F2} CSPR over x402 ({Hash}) for the premium risk read → risk {Risk}",
                    _tick, sig.PaidMotes / 1e9, hash?[..8] ?? "open", sig.Risk);
                await Emit("PAY", $"Paid {sig.PaidMotes / 1e9:F2} CSPR over x402 for the premium risk read → risk {sig.Risk}.", amountCspr: (decimal)sig.PaidMotes / 1_000_000_000m, txHash: hash);
                escalate = sig.Risk == "elevated";
                if (escalate) _log.LogInformation("  elevated risk → escalating to human co-sign.");
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
        _log.LogWarning("tick {T}: POLICY BREACH on {V}… ({Note}) — undelegating {Amt} CSPR back to the vault.", _tick, breach.PublicKey[..12], breach.Note, amount);
        await Emit("PERCEIVE", $"Policy breach on {breach.PublicKey[..10]}… ({breach.Note}) — exiting {amount} CSPR.", validator: breach.PublicKey, amountCspr: amount);

        var validator = PublicKey.FromHexString(breach.PublicKey);
        if (position > cap)
        {
            await Propose(validator, position, undelegate: true, $"exit breaching validator (position {position} > cap {cap})");
            return;
        }
        var r = await _vault.Undelegate(validator, Motes(amount));
        await Audit("UNDELEGATE", breach.PublicKey, amount, r);
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
        _log.LogInformation("tick {T}: deploying {Amt} CSPR to best validator {V}… ({Note}) — routine, ≤ cap.", _tick, amount, best.PublicKey[..12], best.Note);
        var r = await _vault.Delegate(validator, Motes(amount));
        await Audit("DELEGATE", best.PublicKey, amount, r);
        if (r.Success) { _delegated[best.PublicKey] = _delegated.GetValueOrDefault(best.PublicKey) + amount; _idleCspr -= amount; _actions++; }
    }

    private async Task Propose(PublicKey validator, decimal amount, bool undelegate, string why)
    {
        var vHex = validator.ToAccountHex();
        _log.LogInformation("tick {T}: MATERIAL move ({Why}) — proposing on-chain for the human owner to co-sign…", _tick, why);
        var r = await _vault.ProposeMaterial(validator, Motes(amount), undelegate);
        await Audit(undelegate ? "PROPOSE" : "PROPOSE", vHex, amount, r, extra: $" ({why}) — awaits human co-sign");
        if (r.Success)
        {
            var id = _nextProposalId++;
            _feed.State.Proposals.Insert(0, new ProposalView(id, vHex, amount, undelegate, r.Hash, false));
            _actions++;
        }
    }

    private async Task Audit(string kind, string validator, decimal amountCspr, TxResult r, string? extra = null)
    {
        if (r.Success)
        {
            _log.LogInformation("  ✓ {Kind} {Amt} CSPR → {V}…  on-chain: {Url}", kind, amountCspr, validator[..12], r.Url);
            await Emit(kind, $"{kind} {amountCspr} CSPR → {validator[..10]}…{extra}", validator, amountCspr, r.Hash, true);
        }
        else
        {
            _log.LogWarning("  ✗ {Kind} rejected by the leash: {Err}", kind, r.Error);
            await Emit("REJECT", $"{kind} {amountCspr} CSPR rejected by the leash: {r.Error}", validator, amountCspr, r.Hash, false);
        }
    }

    private async Task RefreshState(IReadOnlyList<ValidatorMonitor.Assessment> assessments)
    {
        _feed.State.Actions = _actions;
        _feed.State.Buys = _buys;
        _feed.State.Validators = assessments
            .Select(a => new ValidatorView(a.PublicKey, a.FeePercent, a.IsActive, a.Compliant, _delegated.GetValueOrDefault(a.PublicKey), a.Note))
            .ToList();
        await _feed.PushState();
    }

    private Task Emit(string kind, string message, string? validator = null, decimal? amountCspr = null, string? txHash = null, bool? success = null) =>
        _feed.Push(new AuditEvent(DateTime.UtcNow.ToString("HH:mm:ss"), _tick, kind, message, validator, amountCspr, txHash, success));

    private static ulong Motes(decimal cspr) => (ulong)(cspr * 1_000_000_000m);
}
