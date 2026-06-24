using System.Text.Json;

namespace ChainLeash.Agent;

/// Pure parsing of CSPR.cloud's `/validators?...&includes=account_info` response into a
/// public-key → human name map. Validators register branding (name/url/logo) via the on-chain
/// account-info standard; CSPR.cloud surfaces it under account_info.info.owner.name. Only
/// validators that actually registered a name appear in the map; the rest fall back to the key.
public static class ValidatorNames
{
    /// Map of lowercase public key → registered validator name (non-empty names only).
    public static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var v in data.EnumerateArray())
        {
            if (!v.TryGetProperty("public_key", out var pkEl)) continue;
            var pk = pkEl.GetString();
            if (string.IsNullOrEmpty(pk)) continue;
            if (!v.TryGetProperty("account_info", out var ai) || ai.ValueKind != JsonValueKind.Object) continue;
            if (!ai.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
            if (!info.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object) continue;
            if (!owner.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
            var name = nameEl.GetString();
            if (!string.IsNullOrWhiteSpace(name)) result[pk.ToLowerInvariant()] = name.Trim();
        }
        return result;
    }
}
