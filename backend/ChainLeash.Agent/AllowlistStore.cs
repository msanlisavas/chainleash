using System.Text.Json;

namespace ChainLeash.Agent;

/// Validators the OWNER added at runtime (beyond the config seed), so the agent actually
/// PERCEIVES and can use them. The on-chain `set_validator(true)` gates delegation, but the
/// agent's watch-list is config-driven — without this it would never look at a freshly
/// allowlisted validator. Populated only after an owner `set_validator(true)` is verified
/// on-chain (see /api/owner/confirm), and persisted so it survives a restart.
public sealed class AllowlistStore
{
    private readonly object _lock = new();
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;
    private readonly ILogger<AllowlistStore> _log;
    private bool _saveWarned;

    public AllowlistStore(IConfiguration cfg, ILogger<AllowlistStore> log)
    {
        _log = log;
        var dataPath = cfg["Agent:DataPath"];
        var dir = string.IsNullOrWhiteSpace(dataPath) ? "data" : (Path.GetDirectoryName(dataPath) ?? "data");
        _path = Path.Combine(string.IsNullOrEmpty(dir) ? "data" : dir, "added-validators.json");
        Load();
    }

    /// The owner-added validator public keys (lower-cased).
    public IReadOnlyCollection<string> Added
    {
        get { lock (_lock) return _added.ToArray(); }
    }

    /// Record a newly owner-allowlisted validator so the agent starts watching it. Returns true
    /// if it was new. Only basic shape is enforced here; the genuine authorization is the verified
    /// on-chain set_validator(true) that precedes the call.
    public bool Add(string pubKeyHex)
    {
        var pk = (pubKeyHex ?? "").Trim().ToLowerInvariant();
        if (pk.Length is < 64 or > 70 || !pk.All(Uri.IsHexDigit)) return false;
        lock (_lock)
        {
            if (!_added.Add(pk)) return false;
            Save();
            return true;
        }
    }

    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var arr = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? Array.Empty<string>();
            lock (_lock) foreach (var v in arr) if (!string.IsNullOrWhiteSpace(v)) _added.Add(v.ToLowerInvariant());
        }
        catch { /* corrupt/absent — start from the config seed only */ }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string[] snapshot;
            lock (_lock) snapshot = _added.ToArray();
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            if (!_saveWarned) { _saveWarned = true; _log.LogWarning(ex, "could not persist added-validators to {Path}", _path); }
        }
    }
}
