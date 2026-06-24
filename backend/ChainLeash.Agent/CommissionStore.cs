using System.Text.Json;

namespace ChainLeash.Agent;

/// The owner-set commission-policy threshold (whole percent), persisted. Recorded ONLY after an
/// owner `set_max_commission` tx is verified on-chain (see /api/owner/confirm) — so the on-chain
/// tx is the auditable authorization, and this store is where the agent reads the value (the
/// upgrade-added Odra field isn't reliably raw-readable from the state dict). Falls back to the
/// configured default until the owner sets it.
public sealed class CommissionStore
{
    private readonly object _lock = new();
    private int? _value;
    private readonly string? _path;
    private readonly ILogger<CommissionStore> _log;
    private bool _warned;

    public CommissionStore(IConfiguration cfg, ILogger<CommissionStore> log)
    {
        _log = log;
        var dataPath = cfg["Agent:DataPath"];
        var dir = string.IsNullOrWhiteSpace(dataPath) ? "data" : (Path.GetDirectoryName(dataPath) ?? "data");
        _path = Path.Combine(string.IsNullOrEmpty(dir) ? "data" : dir, "commission-policy.json");
        Load();
    }

    /// The owner-set threshold, or null when never set (the agent then uses its config default).
    public int? Value { get { lock (_lock) return _value; } }

    public void Set(int percent)
    {
        if (percent is < 0 or > 100) return;
        lock (_lock) { _value = percent; Save(); }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try { var v = JsonSerializer.Deserialize<int?>(File.ReadAllText(_path)); lock (_lock) _value = v; }
        catch { /* corrupt/absent → fall back to config */ }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_value));
            if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
        }
        catch (Exception ex) { if (!_warned) { _warned = true; _log.LogWarning(ex, "could not persist commission policy to {Path}", _path); } }
    }
}
