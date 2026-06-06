using Casper.Network.SDK.Types;

namespace ChainLeash.Agent;

/// The autonomous CHAINLEASH staking agent.
///
/// Each tick it reads the vault's REAL state from chain (free balance, committed stake
/// per validator, cap, kill-switch, bond — all gas-free via ChainReader), PERCEIVES the
/// allowlisted validators (live CSPR.cloud metrics), and decides. When it sees an
/// opportunity worth analysing it PAYS over x402 — a real Casper transfer — for a premium
/// risk read ("pay-to-think"); when nothing is actionable it CHOOSES NOT TO ACT.
///
/// On-chain actions: deploy idle treasury to the best compliant validator (≤ cap),
/// redelegate a policy-breaching validator's stake to a better one, or escalate over-cap /
/// elevated-risk moves to a human-co-signed proposal. The agent has no withdraw path and
/// sits below the account's key_management threshold; the leash is enforced by the contract.
/// At startup it posts its slashable bond. Every decision is streamed to the dashboard.
public sealed class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _log;
    private readonly CasperVault _vault;
    private readonly X402Client _x402;
    private readonly ValidatorMonitor _validators;
    private readonly ChainReader _chain;
    private readonly IConfiguration _cfg;
    private readonly IHostApplicationLifetime _life;
    private readonly AuditFeed _feed;

    private int _tick, _actions, _buys;
    private bool _gasWarned;

    public AgentWorker(ILogger<AgentWorker> log, CasperVault vault, X402Client x402, ValidatorMonitor validators,
        ChainReader chain, IConfiguration cfg, IHostApplicationLifetime life, AuditFeed feed)
    {
        _log = log; _vault = vault; _x402 = x402; _validators = validators; _chain = chain; _cfg = cfg; _life = life; _feed = feed;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(_cfg.GetValue("Agent:TickSeconds", 20));
        var chunk = _cfg.GetValue("Staking:DeployChunkCspr", 500m);
        var maxActions = _cfg.GetValue("Agent:MaxOnChainActions", 3);
        var maxBuys = _cfg.GetValue("Agent:MaxSignalBuys", 4);
        var maxTicks = _cfg.GetValue("Agent:MaxTicks", 0);
        var bondTarget = _cfg.GetValue("Staking:BondCspr", 0m);

        _feed.State.PackageHash = (_cfg["Casper:GovernedVaultPackageHash"] ?? "").Replace("hash-", "");
        _feed.State.MaxCommissionPercent = _validators.MaxCommissionPercent;

        _log.LogInformation("CHAINLEASH staking agent online — chunk={Chunk} CSPR, tick={S}s, policy: commission ≤ {Max}%",
            chunk, period.TotalSeconds, _validators.MaxCommissionPercent);
        await Emit("ONLINE", $"Agent online — reading vault state from chain; policy: commission ≤ {_validators.MaxCommissionPercent}%.");

        await MaybePostBond(bondTarget);

        while (!ct.IsCancellationRequested)
        {
            _tick++;
            try { await Tick(chunk, maxActions, maxBuys, ct); }
            catch (Exception ex) { _log.LogError(ex, "tick {T} error", _tick); await Emit("HOLD", $"tick error: {ex.Message}"); }

            if (maxTicks > 0 && _tick >= maxTicks)
            {
                _log.LogInformation("reached MaxTicks={N}. Stopping.", maxTicks);
                _life.StopApplication();
                break;
            }
            try { await Task.Delay(period, ct); } catch (TaskCanceledException) { break; }
        }
    }

    /// Post the agent's slashable bond once, if configured and not already posted on-chain.
    private async Task MaybePostBond(decimal bondTarget)
    {
        if (bondTarget <= 0) return;
        try
        {
            if (await _chain.BondCspr() >= bondTarget) return; // already bonded
            _log.LogInformation("posting {Bond} CSPR slashable bond…", bondTarget);
            var r = await _vault.PostBond((ulong)(bondTarget * 1_000_000_000m));
            await Emit(r.Success ? "BOND" : "REJECT", r.Success
                ? $"Posted {bondTarget} CSPR slashable bond — skin in the game."
                : $"Bond post failed: {r.Error}", amountCspr: bondTarget, txHash: r.Hash, success: r.Success);
        }
        catch (Exception ex) { _log.LogWarning("bond post skipped: {Msg}", ex.Message); }
    }

    private async Task Tick(decimal chunk, int maxActions, int maxBuys, CancellationToken ct)
    {
        // 1) Read the vault's REAL state from chain (gas-free).
        var paused = await _chain.Paused();
        var cap = await _chain.ValueCapCspr();
        var free = await _chain.FreeBalanceCspr();
        var assessments = await _validators.Assess(ct);
        var committed = new Dictionary<string, decimal>();
        foreach (var a in assessments) committed[a.PublicKey] = await _chain.CommittedCspr(a.PublicKey);

        await RefreshState(assessments, committed, cap, free, paused);
        await MaybeWarnLowGas();

        if (paused)
        {
            await Emit("HOLD", "Vault is PAUSED (owner kill-switch) — agent frozen.");
            return;
        }
        if (assessments.Count == 0) { await Emit("HOLD", "No allowlisted validators configured."); return; }

        var breach = assessments.FirstOrDefault(a => !a.Compliant && committed.GetValueOrDefault(a.PublicKey) > 0);
        var best = assessments.FirstOrDefault(a => a.Compliant);
        var hasBreach = breach.PublicKey is not null && committed.GetValueOrDefault(breach.PublicKey) > 0;
        var canDeploy = StakingPolicy.CanDeploy(free, chunk, best.PublicKey is not null, best.Compliant);

        if (!hasBreach && !canDeploy)
        {
            var seen = best.PublicKey is not null ? $"best {best.PublicKey[..10]}… ({best.Note})" : "no compliant validator";
            await Emit("HOLD", $"Chain: free {free:N0} CSPR, {assessments.Count} validators, {seen}; nothing off-policy → chose not to act (no spend).");
            return;
        }
        if (_actions >= maxActions) { await Emit("HOLD", $"On-chain action budget spent ({_actions}) → holding."); return; }

        // 2) Pay-to-think: an action is warranted, so buy the premium risk read.
        var escalate = false;
        if (_buys < maxBuys)
        {
            try
            {
                _buys++;
                var sig = await _x402.BuySignal(best.PublicKey); // risk read for the validator we're about to act on
                _feed.State.Buys = _buys;
                _feed.State.X402SpentCspr += (decimal)sig.PaidMotes / 1_000_000_000m;
                var hash = string.IsNullOrEmpty(sig.SettlementHash) ? null : sig.SettlementHash;
                await Emit("PAY", $"Paid {sig.PaidMotes / 1e9:F2} CSPR over x402 for the premium risk read → risk {sig.Risk}.",
                    amountCspr: (decimal)sig.PaidMotes / 1_000_000_000m, txHash: hash);
                escalate = sig.Risk == "elevated";
            }
            catch (Exception ex) { _log.LogWarning("x402 read unavailable ({Msg}) — proceeding on free policy signal.", ex.Message); }
        }

        if (hasBreach) await ExitBreach(breach, best, committed.GetValueOrDefault(breach.PublicKey!), cap);
        else await Deploy(best, StakingPolicy.DeployAmount(free, chunk), cap, escalate);
    }

    /// A delegated validator broke policy → redelegate its stake to the best compliant
    /// validator (single native tx); undelegate to the vault if none is compliant.
    private async Task ExitBreach(ValidatorMonitor.Assessment breach, ValidatorMonitor.Assessment best, decimal position, decimal cap)
    {
        var amount = StakingPolicy.ExitAmount(position, cap);
        var from = PublicKey.FromHexString(breach.PublicKey);
        var hasDestination = best.PublicKey is not null && best.Compliant && best.PublicKey != breach.PublicKey;

        if (StakingPolicy.MustEscalateExit(position, cap))
        {
            await Emit("PERCEIVE", $"Policy breach on {breach.PublicKey[..10]}… ({breach.Note}); position {position} > cap → escalating exit.", validator: breach.PublicKey, amountCspr: position);
            await Propose(from, position, undelegate: true, $"exit breaching validator (position {position} > cap {cap})");
            return;
        }
        if (hasDestination)
        {
            var bestPk = best.PublicKey!;
            await Emit("PERCEIVE", $"Policy breach on {breach.PublicKey[..10]}… ({breach.Note}) — redelegating {amount} CSPR → {bestPk[..10]}… ({best.Note}).", validator: breach.PublicKey, amountCspr: amount);
            var r = await _vault.Redelegate(from, PublicKey.FromHexString(bestPk), Motes(amount));
            await Audit("REDELEGATE", bestPk, amount, r);
            if (r.Success) _actions++;
            return;
        }
        var u = await _vault.Undelegate(from, Motes(amount));
        await Audit("UNDELEGATE", breach.PublicKey, amount, u);
        if (u.Success) _actions++;
    }

    /// Idle treasury + a compliant best validator → deploy a chunk into it (or escalate).
    private async Task Deploy(ValidatorMonitor.Assessment best, decimal amount, decimal cap, bool escalate)
    {
        var validator = PublicKey.FromHexString(best.PublicKey);
        if (StakingPolicy.RequiresProposal(amount, cap, escalate))
        {
            await Propose(validator, amount, undelegate: false, amount > cap ? $"deploy {amount} > cap {cap}" : "elevated risk read → human co-sign");
            return;
        }
        _log.LogInformation("tick {T}: deploying {Amt} CSPR to {V}… ({Note}) — routine, ≤ cap.", _tick, amount, best.PublicKey[..12], best.Note);
        var r = await _vault.Delegate(validator, Motes(amount));
        await Audit("DELEGATE", best.PublicKey, amount, r);
        if (r.Success) _actions++;
    }

    private async Task Propose(PublicKey validator, decimal amount, bool undelegate, string why)
    {
        var vHex = validator.ToAccountHex();
        _log.LogInformation("tick {T}: MATERIAL move ({Why}) — proposing for human co-sign…", _tick, why);
        var r = await _vault.ProposeMaterial(validator, Motes(amount), undelegate);
        await Audit("PROPOSE", vHex, amount, r, extra: $" ({why}) — awaits human co-sign");
        if (r.Success)
        {
            var id = await _chain.NextProposalId(); // chain-truth id (next-1 is the one just created)
            _feed.State.Proposals.Insert(0, new ProposalView(id == 0 ? 0 : id - 1, vHex, amount, undelegate, r.Hash, false));
            _actions++;
        }
    }

    private async Task Audit(string kind, string validator, decimal amountCspr, TxResult r, string? extra = null)
    {
        if (r.Success)
        {
            _log.LogInformation("  ✓ {Kind} {Amt} CSPR → {V}…  {Url}", kind, amountCspr, validator[..12], r.Url);
            await Emit(kind, $"{kind} {amountCspr} CSPR → {validator[..10]}…{extra}", validator, amountCspr, r.Hash, true);
        }
        else
        {
            _log.LogWarning("  ✗ {Kind} rejected by the leash: {Err}", kind, r.Error);
            await Emit("REJECT", $"{kind} {amountCspr} CSPR rejected by the leash: {r.Error}", validator, amountCspr, r.Hash, false);
        }
    }

    private async Task RefreshState(IReadOnlyList<ValidatorMonitor.Assessment> assessments, Dictionary<string, decimal> committed,
        decimal cap, decimal free, bool paused)
    {
        var s = _feed.State;
        s.Actions = _actions;
        s.Buys = _buys;
        s.CapCspr = cap;
        s.Paused = paused;
        s.FreeBalanceCspr = free;
        try
        {
            s.BondCspr = await _chain.BondCspr();
            s.TotalBalanceCspr = await _chain.TotalBalanceCspr();
            s.MaxPerValidatorCspr = await _chain.MaxPerValidatorCspr();
            s.Violations = (int)await _chain.Violations();
            s.AgentGasCspr = await _chain.AccountBalanceCspr(_vault.AgentKey.ToAccountHex());
        }
        catch { /* keep last-known on a transient read failure */ }
        s.Validators = assessments
            .Select(a => new ValidatorView(a.PublicKey, a.FeePercent, a.IsActive, a.Compliant, committed.GetValueOrDefault(a.PublicKey), a.Note))
            .ToList();
        await _feed.PushState();
    }

    /// Warn once when the agent's gas wallet dips below the threshold (edge-triggered).
    private async Task MaybeWarnLowGas()
    {
        var warn = _cfg.GetValue("Agent:LowGasWarnCspr", 50m);
        var gas = _feed.State.AgentGasCspr;
        if (gas > 0 && gas < warn)
        {
            if (!_gasWarned)
            {
                await Emit("HOLD", $"⛽ Agent gas low: {gas:N0} CSPR (< {warn:N0}). Top up the agent account so it can keep signing transactions.");
                _gasWarned = true;
            }
        }
        else if (gas >= warn) _gasWarned = false;
    }

    private Task Emit(string kind, string message, string? validator = null, decimal? amountCspr = null, string? txHash = null, bool? success = null) =>
        _feed.Push(new AuditEvent(DateTime.UtcNow.ToString("HH:mm:ss"), _tick, kind, message, validator, amountCspr, txHash, success));

    private static ulong Motes(decimal cspr) => (ulong)(cspr * 1_000_000_000m);
}
