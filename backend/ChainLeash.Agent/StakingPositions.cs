using System.Globalization;
using System.Text.Json;

namespace ChainLeash.Agent;

/// Pure parsing for the staking page: find the VAULT's own delegation inside a CSPR.cloud
/// `/validators/{v}/delegations` response. The vault delegates from a CONTRACT purse, so its
/// delegator_identifier is a purse UREF (type id 1), not a public key — which is exactly why
/// block explorers keyed by public key can't show it.
public static class StakingPositions
{
    /// The 64-hex address of a formatted uref ("uref-<hex>-NNN"), lower-cased; "" if unparseable.
    public static string UrefAddress(string? uref)
    {
        if (string.IsNullOrWhiteSpace(uref)) return "";
        var s = uref.Trim();
        if (s.StartsWith("uref-", StringComparison.OrdinalIgnoreCase)) s = s["uref-".Length..];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return s.ToLowerInvariant();
    }

    /// The vault's current stake (CSPR) on one validator, from that validator's delegations
    /// JSON, matched by the vault's purse uref ADDRESS (access-rights-suffix insensitive).
    /// 0 if the vault has no delegation there. Throws JsonException only on a malformed body.
    public static decimal VaultStakeCspr(string json, string vaultUrefAddress)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return 0m;
        foreach (var row in data.EnumerateArray())
        {
            if (!row.TryGetProperty("delegator_identifier", out var idEl)) continue;
            if (UrefAddress(idEl.GetString()) != vaultUrefAddress) continue;
            if (!row.TryGetProperty("stake", out var stakeEl)) continue;
            var motes = stakeEl.ValueKind == JsonValueKind.String ? stakeEl.GetString() : stakeEl.ToString();
            return decimal.TryParse(motes, NumberStyles.Any, CultureInfo.InvariantCulture, out var m)
                ? m / 1_000_000_000m : 0m;
        }
        return 0m;
    }
}
