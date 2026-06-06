using Microsoft.AspNetCore.SignalR;

namespace ChainLeash.Agent;

/// One entry in the agent's on-chain audit trail, streamed live to the dashboard.
public sealed record AuditEvent(
    string Time,
    int Tick,
    string Kind,          // ONLINE | PERCEIVE | PAY | DELEGATE | UNDELEGATE | PROPOSE | HOLD | REJECT
    string Message,
    string? Validator = null,
    decimal? AmountCspr = null,
    string? TxHash = null,
    bool? Success = null)
{
    public string? TxUrl => TxHash is null ? null : $"https://testnet.cspr.live/transaction/{TxHash}";
}

/// A validator as the agent currently sees it (live CSPR.cloud metric + policy verdict).
public sealed record ValidatorView(string PublicKey, int FeePercent, bool Active, bool Compliant, decimal DelegatedCspr, string Note);

/// A pending material (over-cap / escalated) proposal awaiting the human owner's co-sign.
public sealed record ProposalView(uint Id, string Validator, decimal AmountCspr, bool Undelegate, string TxHash, bool Resolved);

/// Live leash + agent state the dashboard renders.
public sealed class FeedState
{
    public string PackageHash { get; set; } = "";
    public decimal CapCspr { get; set; }            // value_cap — read from chain
    public int MaxCommissionPercent { get; set; }
    public decimal X402SpentCspr { get; set; }
    public int Actions { get; set; }
    public int Buys { get; set; }
    // --- full leash state, all read from chain (no config) ---
    public bool Paused { get; set; }                // owner kill-switch
    public decimal BondCspr { get; set; }           // posted slashable bond
    public decimal FreeBalanceCspr { get; set; }    // withdrawable (liquid − bond)
    public decimal TotalBalanceCspr { get; set; }   // liquid vault purse
    public decimal MaxPerValidatorCspr { get; set; }// per-validator ceiling (0 = unlimited)
    public int Violations { get; set; }
    public List<ValidatorView> Validators { get; set; } = new();
    public List<ProposalView> Proposals { get; set; } = new();
}

/// SignalR hub the dashboard subscribes to: receives "audit" (per event) and "state".
public sealed class AuditHub : Hub { }

/// Singleton sink the agent writes to; keeps recent history and broadcasts over SignalR.
public sealed class AuditFeed
{
    private readonly IHubContext<AuditHub> _hub;
    private readonly object _lock = new();
    private readonly LinkedList<AuditEvent> _events = new();

    public FeedState State { get; } = new();

    public AuditFeed(IHubContext<AuditHub> hub) => _hub = hub;

    public IReadOnlyList<AuditEvent> Recent()
    {
        lock (_lock) return _events.ToList();
    }

    public async Task Push(AuditEvent e)
    {
        lock (_lock)
        {
            _events.AddFirst(e);
            while (_events.Count > 200) _events.RemoveLast();
        }
        await _hub.Clients.All.SendAsync("audit", e);
    }

    public Task PushState() => _hub.Clients.All.SendAsync("state", State);
}
