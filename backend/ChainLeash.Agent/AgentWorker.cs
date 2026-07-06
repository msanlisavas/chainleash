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
    private int _failStreak;      // consecutive failed on-chain actions → exponential backoff
    private int _backoffUntilTick;
    private string? _lastTickError; // edge-trigger the tick-error HOLD so an outage doesn't flood the feed
    private string? _lastIdleHold;  // de-dupe the idle "nothing off-policy" HOLD so steady-state doesn't bury the real story (the live heartbeat shows the agent is still watching)
    private DateTime _lastIdleHoldAt; // …but re-print the idle status at least this often so a de-duped feed never LOOKS frozen for days
    private string? _lastVanishedNotice; // de-dupe the "validator left the era set with stale committed" advisory
    private DateTime _lastVanishedAt;
    private static readonly TimeSpan IdleHoldRefresh = TimeSpan.FromMinutes(30);
    private const int GasCheckEveryNTicks = 6; // agent gas changes only on spend → refresh ~hourly at 600s, not every tick

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
        // Report the EFFECTIVE threshold (owner's wallet-set value if any, else config) — the same
        // value the agent actually enforces and RefreshState streams — not the raw config default,
        // so the startup banner can't contradict the live "now ≤ N%" the owner set on-chain.
        var effectiveMaxCommission = _validators.EffectiveMaxCommission();
        _feed.State.MaxCommissionPercent = effectiveMaxCommission;

        _log.LogInformation("CHAINLEASH staking agent online — chunk={Chunk} CSPR, tick={S}s, policy: commission ≤ {Max}%",
            chunk, period.TotalSeconds, effectiveMaxCommission);
        await Emit("ONLINE", $"Agent online — reading vault state from chain; policy: commission ≤ {effectiveMaxCommission}%.");

        if (_vault.ReadOnly)
            await Emit("HOLD", "Observer mode — no agent key configured. Reading the live vault read-only; the agent signs nothing. Add an agent key (see RUNBOOK) and point at your own vault to enable on-chain moves.");
        else
            await MaybePostBond(bondTarget);

        while (!ct.IsCancellationRequested)
        {
            _tick++;
            try
            {
                await Tick(chunk, maxActions, maxBuys, ct);
                if (_lastTickError is not null)
                {
                    // Recovered from a tick-error episode (e.g. a transient node hiccup that the
                    // fail-safe correctly HELD on). Emit a RESUMED line so a days-old red error stops
                    // dominating the feed, and force the next idle status to re-print fresh.
                    _lastTickError = null;
                    _lastIdleHold = null;
                    await Emit("ONLINE", "Chain reads recovered — resumed watching the vault.");
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "tick {T} error", _tick);
                // The strict ChainReader throws instead of fabricating defaults — so a
                // failed tick means the dashboard's numbers are last-known, not live.
                _feed.State.Stale = true;
                if (ex.Message != _lastTickError) // edge-triggered: one HOLD per distinct failure
                {
                    _lastTickError = ex.Message;
                    await Emit("HOLD", $"tick error: {ex.Message}");
                }
                await _feed.PushState();
            }

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
        _feed.State.LastCheckedIso = DateTime.UtcNow.ToString("o"); // heartbeat: the agent evaluated the vault now
        // 1) Read the vault's REAL state from chain (gas-free). A failed read THROWS
        //    (ChainRpcException) — the agent must never act on fabricated defaults like
        //    "kill-switch off" or "cap 0"; the loop's catch emits HOLD and flags Stale.
        var paused = await _chain.Paused();
        var cap = await _chain.ValueCapCspr();
        // Read the vault purse balance + bond ONCE; free = total − bond. (RefreshState used to
        // re-read both — two extra node-RPC calls per tick for values already in hand.)
        var total = await _chain.TotalBalanceCspr();
        var bond = await _chain.BondCspr();
        var free = total - bond;
        var assessments = await _validators.Assess(ct);
        var committed = new Dictionary<string, decimal>();
        foreach (var a in assessments) committed[a.PublicKey] = await _chain.CommittedCspr(a.PublicKey);

        await RefreshState(assessments, committed, cap, total, bond, free, paused);
        await MaybeWarnLowGas();

        if (_vault.ReadOnly) return; // observer mode: perceive + stream live state, never act

        if (paused)
        {
            await Emit("HOLD", "Vault is PAUSED (owner kill-switch) — agent frozen.");
            return;
        }
        if (assessments.Count == 0) { await Emit("HOLD", "No allowlisted validators configured."); return; }

        // Validators that LEFT the era set but still carry directed `committed` stake. The agent
        // can't exit these (the bid may be gone → undelegate reverts), so it surfaces them for the
        // owner to reconcile: Recall (if merely evicted) or Clear stale committed (if the bid is gone).
        var vanished = assessments
            .Where(a => !StakingPolicy.CanAgentAutoExit(a.IsActive) && committed.GetValueOrDefault(a.PublicKey) > 0)
            .ToList();
        if (vanished.Count > 0)
        {
            var summary = string.Join("; ", vanished.Select(a => $"{a.PublicKey[..10]}… {committed.GetValueOrDefault(a.PublicKey):N0} CSPR"));
            var msg = $"{vanished.Count} validator(s) left the era set with directed stake ({summary}). The agent can't exit these — the bid may be gone. Owner: Recall if merely evicted, or Clear stale committed if it left the auction.";
            if (msg != _lastVanishedNotice || DateTime.UtcNow - _lastVanishedAt > IdleHoldRefresh)
            {
                _lastVanishedNotice = msg; _lastVanishedAt = DateTime.UtcNow;
                await Emit("HOLD", msg);
            }
        }

        if (_tick < _backoffUntilTick)
        {
            _log.LogInformation("tick {T}: backing off after {N} failed action(s) until tick {U}", _tick, _failStreak, _backoffUntilTick);
            return; // backoff already announced via HOLD when it started
        }

        // Decide whether there's anything to do FIRST — at a per-era cadence most ticks are HOLDs,
        // and a HOLD tick must not spend node-RPCs reading the on-chain cooldown it will never use.
        // A validator with an already-queued (unresolved) material proposal must NOT be acted on
        // again: a >cap breach is escalated to a co-sign proposal that leaves `committed` intact
        // (only the owner's approval moves the stake), so without this guard the agent would
        // re-propose the same exit every tick — spamming the co-sign queue and starving routine
        // deploys. Treat a validator with a pending proposal as handled until the owner resolves it.
        var pendingProposalValidators = new HashSet<string>(
            _feed.State.Proposals.Where(p => !p.Resolved).Select(p => p.Validator),
            StringComparer.OrdinalIgnoreCase);
        // Only auto-exit a validator whose bid still exists (active). A non-compliant INACTIVE
        // validator may have withdrawn its bid — any undelegate/redelegate reverts ValidatorNotFound
        // (2026-07 incident), so the agent hands it to the owner instead of escalating a doomed exit.
        var breach = assessments.FirstOrDefault(a => !a.Compliant && StakingPolicy.CanAgentAutoExit(a.IsActive)
            && committed.GetValueOrDefault(a.PublicKey) > 0
            && !pendingProposalValidators.Contains(a.PublicKey));
        var minDelegation = _cfg.GetValue("Staking:MinDelegationCspr", 500m);
        var deployAmt = StakingPolicy.DeployAmount(free, chunk);
        // Prefer a compliant validator this deploy will actually BOND into: topping up an existing
        // position always clears the network minimum, but opening a NEW one needs amount > minDelegation
        // — else the auction rejects it (DelegationAmountTooSmall) and the stake strands as phantom
        // `committed` (the 0147c incident). Fall back to any compliant validator so the breach/idle
        // paths below behave exactly as before when nothing is stickable.
        var stickableBest = assessments.FirstOrDefault(a => a.Compliant
                     && StakingPolicy.BondWouldStick(deployAmt, committed.GetValueOrDefault(a.PublicKey), minDelegation));
        var best = stickableBest.PublicKey is not null ? stickableBest : assessments.FirstOrDefault(a => a.Compliant);
        var bestExisting = best.PublicKey is not null ? committed.GetValueOrDefault(best.PublicKey) : 0m;
        var hasBreach = breach.PublicKey is not null && committed.GetValueOrDefault(breach.PublicKey) > 0;
        var canDeploy = StakingPolicy.CanDeploy(free, chunk, best.PublicKey is not null, best.Compliant);

        if (!hasBreach && !canDeploy)
        {
            var seen = best.PublicKey is not null ? $"best {best.PublicKey[..10]}… ({best.Note})" : "no compliant validator";
            var msg = $"Chain: free {free:N0} CSPR, {assessments.Count} validators, {seen}; nothing off-policy → chose not to act (no spend).";
            // Only log a fresh HOLD when the decision actually changes (e.g. free balance moved); a
            // run of identical idle ticks would otherwise bury the real moves. The live heartbeat
            // (LastCheckedIso, pushed every tick by RefreshState) shows the agent is still watching.
            // But re-print at least every IdleHoldRefresh so a long de-duped run never LOOKS frozen —
            // the "positions/rewards stuck" report was a static feed, not a stalled agent.
            if (msg != _lastIdleHold || DateTime.UtcNow - _lastIdleHoldAt > IdleHoldRefresh)
            {
                _lastIdleHold = msg; _lastIdleHoldAt = DateTime.UtcNow;
                await Emit("HOLD", msg);
            }
            return;
        }
        if (_actions >= maxActions) { await Emit("HOLD", $"On-chain action budget spent ({_actions}) → holding."); return; }

        // There IS a move to make — now (and only now) read the vault's on-chain cooldown, so we
        // don't sign a move the contract will revert with RateLimited (a revert still burns gas).
        var intervalMs = await _chain.ActionIntervalMs();
        if (intervalMs > 0)
        {
            var lastMs = await _chain.LastActionTimeMs();
            var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (lastMs != 0 && nowMs < lastMs + intervalMs)
            {
                _log.LogInformation("tick {T}: vault cooldown active ({Left}s left) → holding", _tick, (lastMs + intervalMs - nowMs) / 1000);
                return;
            }
        }

        // 2) Pay-to-think: an action is warranted, so buy the premium risk read.
        var escalate = false;
        if (_buys < maxBuys)
        {
            try
            {
                _buys++;
                var sig = await _x402.BuySignal(best.PublicKey, ct); // risk read for the validator we're about to act on
                _feed.State.Buys = _buys;
                _feed.State.X402SpentCspr += (decimal)sig.PaidMotes / 1_000_000_000m;
                var hash = string.IsNullOrEmpty(sig.SettlementHash) ? null : sig.SettlementHash;
                await Emit("PAY", $"Paid {sig.PaidMotes / 1e9:F2} CSPR over x402 for the premium risk read → risk {sig.Risk}.",
                    amountCspr: (decimal)sig.PaidMotes / 1_000_000_000m, txHash: hash);
                escalate = sig.Risk == "elevated";
            }
            catch (X402StrandedPaymentException stranded)
            {
                // The CSPR is SPENT (settlement confirmed) even though no signal came back —
                // record it so the books stay honest, and surface the redeemable proof.
                _feed.State.Buys = _buys;
                _feed.State.X402SpentCspr += (decimal)stranded.PaidMotes / 1_000_000_000m;
                await Emit("PAY", $"Paid {stranded.PaidMotes / 1e9:F2} CSPR over x402 but no signal was served — proof {stranded.SettlementHash[..10]}… remains redeemable.",
                    amountCspr: (decimal)stranded.PaidMotes / 1_000_000_000m, txHash: stranded.SettlementHash, success: false);
                _log.LogWarning("x402 stranded payment: {Msg} — proceeding on free policy signal.", stranded.Message);
            }
            catch (Exception ex) { _log.LogWarning("x402 read unavailable ({Msg}) — proceeding on free policy signal.", ex.Message); }
        }

        if (hasBreach) await ExitBreach(breach, best, committed.GetValueOrDefault(breach.PublicKey!), bestExisting, cap, minDelegation, ct);
        else await Deploy(best, deployAmt, cap, bestExisting, minDelegation, escalate, ct);
    }

    /// Track action outcomes: success resets the failure streak; a failed (reverted or
    /// unconfirmed) action backs the agent off exponentially — repeating a paid revert
    /// every 20s tick burns gas for nothing and floods the audit trail.
    private async Task TrackOutcome(TxResult r)
    {
        if (r.Success) { _failStreak = 0; return; }
        _failStreak = Math.Min(_failStreak + 1, 8); // clamp: C# masks shift counts, and the display count should stay sane
        var skip = 1 << Math.Min(_failStreak, 4);   // 2,4,8,16 ticks ≈ up to ~5 min at 20s
        _backoffUntilTick = _tick + skip;
        await Emit("HOLD", $"Backing off {skip} ticks after {_failStreak} failed action(s) — last error: {r.Error}");
    }

    /// A delegated validator broke policy → redelegate its stake to the best compliant
    /// validator (single native tx); undelegate to the vault if none is compliant.
    private async Task ExitBreach(ValidatorMonitor.Assessment breach, ValidatorMonitor.Assessment best, decimal position, decimal bestExisting, decimal cap, decimal minDelegation, CancellationToken ct)
    {
        var amount = StakingPolicy.ExitAmount(position, cap);
        var from = PublicKey.FromHexString(breach.PublicKey);
        var hasDestination = best.PublicKey is not null && best.Compliant && best.PublicKey != breach.PublicKey;

        if (StakingPolicy.MustEscalateExit(position, cap))
        {
            await Emit("PERCEIVE", $"Policy breach on {breach.PublicKey[..10]}… ({breach.Note}); position {position} > cap → escalating exit.", validator: breach.PublicKey, amountCspr: position);
            await Propose(from, position, undelegate: true, $"exit breaching validator (position {position} > cap {cap})", ct);
            return;
        }
        // Redelegate ONLY into a destination the stake will actually bond into — an existing position,
        // or a new one above the network minimum. Otherwise the redelegate's deferred re-bond fails
        // (DelegationAmountTooSmall) and the stake strands as phantom committed (0147c incident); pull it
        // back to the vault instead, which always bonds to the purse.
        if (hasDestination && StakingPolicy.BondWouldStick(amount, bestExisting, minDelegation))
        {
            var bestPk = best.PublicKey!;
            await Emit("PERCEIVE", $"Policy breach on {breach.PublicKey[..10]}… ({breach.Note}) — redelegating {amount} CSPR → {bestPk[..10]}… ({best.Note}).", validator: breach.PublicKey, amountCspr: amount);
            var r = await _vault.Redelegate(from, PublicKey.FromHexString(bestPk), Motes(amount), ct);
            await Audit("REDELEGATE", bestPk, amount, r);
            if (r.Success) _actions++;
            await TrackOutcome(r);
            return;
        }
        if (hasDestination) // there was a compliant destination, but a redelegate there wouldn't bond
            await Emit("PERCEIVE", $"Policy breach on {breach.PublicKey[..10]}… — best destination {best.PublicKey![..10]}… is a new position below the {minDelegation:N0} CSPR minimum; pulling {amount} CSPR back to the vault instead of stranding it.", validator: breach.PublicKey, amountCspr: amount);
        var u = await _vault.Undelegate(from, Motes(amount), ct);
        await Audit("UNDELEGATE", breach.PublicKey, amount, u);
        if (u.Success) _actions++;
        await TrackOutcome(u);
    }

    /// Idle treasury + a compliant best validator → deploy a chunk into it (or escalate).
    private async Task Deploy(ValidatorMonitor.Assessment best, decimal amount, decimal cap, decimal existingPosition, decimal minDelegation, bool escalate, CancellationToken ct)
    {
        if (!StakingPolicy.BondWouldStick(amount, existingPosition, minDelegation))
        {
            // Opening a NEW position with a chunk at/below the network minimum is rejected on-chain
            // (DelegationAmountTooSmall) and would strand the stake as phantom committed. Hold and let
            // free balance accumulate until it clears the minimum (or an existing position to top up appears).
            await Emit("HOLD", $"Best compliant validator {best.PublicKey![..10]}… has no existing position; a new delegation of {amount:N0} CSPR ≤ the {minDelegation:N0} CSPR network minimum would be rejected on-chain. Holding to accumulate before opening it.");
            return;
        }
        var validator = PublicKey.FromHexString(best.PublicKey);
        if (StakingPolicy.RequiresProposal(amount, cap, escalate))
        {
            await Propose(validator, amount, undelegate: false, amount > cap ? $"deploy {amount} > cap {cap}" : "elevated risk read → human co-sign", ct);
            return;
        }
        _log.LogInformation("tick {T}: deploying {Amt} CSPR to {V}… ({Note}) — routine, ≤ cap.", _tick, amount, best.PublicKey[..12], best.Note);
        var r = await _vault.Delegate(validator, Motes(amount), ct);
        await Audit("DELEGATE", best.PublicKey, amount, r);
        if (r.Success) _actions++;
        await TrackOutcome(r);
    }

    private async Task Propose(PublicKey validator, decimal amount, bool undelegate, string why, CancellationToken ct)
    {
        var vHex = validator.ToAccountHex();
        _log.LogInformation("tick {T}: MATERIAL move ({Why}) — proposing for human co-sign…", _tick, why);
        // Read the id BEFORE proposing: only this agent proposes to the vault, so the next
        // id is the one this proposal will get (the post-hoc next-1 read raced the chain).
        uint? id = null;
        try { id = await _chain.NextProposalId(); } catch { /* fall back to the chain rebuild below */ }
        var r = await _vault.ProposeMaterial(validator, Motes(amount), undelegate, ct);
        await Audit("PROPOSE", vHex, amount, r, extra: $" ({why}) — awaits human co-sign");
        if (r.Success)
        {
            // Optimistic insert so the co-sign card appears immediately; the next tick's
            // RefreshState rebuilds the whole list from chain truth regardless.
            if (id is not null)
                _feed.State.Proposals = _feed.State.Proposals
                    .Where(p => p.Id != id.Value)
                    .Prepend(new ProposalView(id.Value, vHex, amount, undelegate, r.Hash, false))
                    .ToList();
            _actions++;
        }
        await TrackOutcome(r);
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
        decimal cap, decimal total, decimal bond, decimal free, bool paused)
    {
        var s = _feed.State;
        s.Actions = _actions;
        s.Buys = _buys;
        s.CapCspr = cap;
        s.MaxCommissionPercent = _validators.EffectiveMaxCommission(); // owner wallet-set threshold if set, else config
        s.Paused = paused;
        s.FreeBalanceCspr = free;
        s.TotalBalanceCspr = total; // already read this tick — don't re-fetch
        s.BondCspr = bond;          // already read this tick — don't re-fetch
        try
        {
            s.MaxPerValidatorCspr = await _chain.MaxPerValidatorCspr(); // long-TTL cached in ChainReader
            s.ActionIntervalMs = await _chain.ActionIntervalMs();       // long-TTL cached in ChainReader
            s.Violations = (int)await _chain.Violations();              // long-TTL cached in ChainReader
            // Agent gas changes only when the agent spends — refresh ~hourly (every Nth tick), keeping
            // the last value otherwise; the low-gas warning is edge-triggered so this is ample.
            if (_vault.AgentKey is null) s.AgentGasCspr = 0;
            else if (_tick % GasCheckEveryNTicks == 1 || s.AgentGasCspr == 0)
                s.AgentGasCspr = await _chain.AccountBalanceCspr(_vault.AgentKey.ToAccountHex());
            // The co-sign queue is CHAIN truth — it survives restarts and can't be forged
            // by replaying old co-sign hashes. Known propose-tx hashes are kept for links.
            var known = s.Proposals.ToDictionary(p => p.Id, p => p.TxHash);
            s.Proposals = (await _chain.Proposals())
                .Select(cp => new ProposalView(cp.Id, cp.Proposal.ValidatorHex, cp.Proposal.AmountCspr,
                    cp.Proposal.Undelegate, known.GetValueOrDefault(cp.Id, ""), cp.Proposal.Resolved))
                .ToList();
            s.Stale = false;
        }
        catch { s.Stale = true; /* keep last-known values, but flag them as possibly stale */ }
        s.Validators = assessments
            .Select(a => new ValidatorView(a.PublicKey, a.FeePercent, a.IsActive, a.Compliant, committed.GetValueOrDefault(a.PublicKey), a.Note, a.Name, a.Allowed))
            .ToList();
        await _feed.PushState();
    }

    /// Warn once when the agent's gas wallet dips below the threshold (edge-triggered).
    /// A fully DRAINED wallet (a real 0 from a successful balance read) is the loudest
    /// case of all — but an UNREAD default of 0 (Stale tick) must never false-alarm.
    private async Task MaybeWarnLowGas()
    {
        if (_vault.ReadOnly) return;       // observer mode has no gas wallet to monitor
        if (_feed.State.Stale) return;     // last read failed → 0 may just mean "not read"
        var warn = _cfg.GetValue("Agent:LowGasWarnCspr", 50m);
        var gas = _feed.State.AgentGasCspr;
        if (gas < warn)
        {
            if (!_gasWarned)
            {
                await Emit("HOLD", $"⛽ Agent gas low: {gas:N0} CSPR (< {warn:N0}). Top up the agent account so it can keep signing transactions.");
                _gasWarned = true;
            }
        }
        else _gasWarned = false;
    }

    private Task Emit(string kind, string message, string? validator = null, decimal? amountCspr = null, string? txHash = null, bool? success = null) =>
        _feed.Push(new AuditEvent(DateTime.UtcNow.ToString("HH:mm:ss"), _tick, kind, message, validator, amountCspr, txHash, success,
            DateTime.UtcNow.ToString("o")));

    private static ulong Motes(decimal cspr) => (ulong)(cspr * 1_000_000_000m);
}
