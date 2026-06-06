using System.Text.Json;
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
    public decimal AgentGasCspr { get; set; }       // agent account balance (pays tx gas) — ops/health
    public bool Stale { get; set; }                 // true if the last chain read failed (values may be stale)
    public List<ValidatorView> Validators { get; set; } = new();
    public List<ProposalView> Proposals { get; set; } = new();
}

/// SignalR hub the dashboard subscribes to: receives "audit" (per event) and "state".
public sealed class AuditHub : Hub { }

/// On-disk snapshot so the audit trail + cumulative x402 spend survive a restart.
/// (Leash state is re-read from chain each tick, so it isn't persisted.)
file sealed record FeedSnapshot(List<AuditEvent> Events, decimal X402SpentCspr);

/// Singleton sink the agent writes to; keeps recent history, persists it, and broadcasts
/// over SignalR. The audit log is restored on startup so the dashboard isn't blank.
public sealed class AuditFeed
{
    private readonly IHubContext<AuditHub> _hub;
    private readonly object _lock = new();
    private readonly LinkedList<AuditEvent> _events = new();
    private readonly string? _path;

    public FeedState State { get; } = new();

    public AuditFeed(IHubContext<AuditHub> hub, IConfiguration cfg)
    {
        _hub = hub;
        var configured = cfg["Agent:DataPath"];
        _path = string.IsNullOrWhiteSpace(configured) ? Path.Combine("data", "feed.json") : configured;
        Load();
    }

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
        Save();
        await _hub.Clients.All.SendAsync("audit", e);
    }

    public Task PushState()
    {
        Save(); // cumulative x402 spend lives on State and changes between events
        return _hub.Clients.All.SendAsync("state", State);
    }

    private void Load()
    {
        if (_path is null) return;
        // Try the primary, then the backup — so a truncated primary (crash mid-write) doesn't
        // silently zero the audit trail + cumulative spend.
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var snap = JsonSerializer.Deserialize<FeedSnapshot>(File.ReadAllText(path));
                if (snap is null) continue;
                lock (_lock)
                {
                    _events.Clear();
                    // file stores newest-first; AddLast preserves that order
                    foreach (var e in snap.Events.Take(200)) _events.AddLast(e);
                }
                State.X402SpentCspr = snap.X402SpentCspr;
                return;
            }
            catch { /* corrupt — fall through to the backup */ }
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            List<AuditEvent> snapshot;
            lock (_lock) snapshot = _events.ToList();
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new FeedSnapshot(snapshot, State.X402SpentCspr));
            // Atomic: write a temp file then swap it in (keeping a .bak), so a crash mid-write
            // never leaves the live snapshot truncated.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
            else File.Move(tmp, _path);
        }
        catch { /* best-effort persistence — never break the live feed on an I/O hiccup */ }
    }
}
