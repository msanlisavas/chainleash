using System.Text.RegularExpressions;

namespace ChainLeash.Agent;

/// Translates the chain's raw revert codes into the GovernedVault's named errors for the
/// audit feed. In Odra 2.7 a `#[odra::odra_error]` USER error reverts with its RAW
/// discriminant (`User error: 4` == OverCap); only Odra's internal framework errors are
/// offset into the 64536+ range (e.g. 64658 == MissingArg). An operator (or judge)
/// reading the feed should see `OverCap`, not a bare number.
public static partial class OdraErrors
{
    private static readonly Dictionary<int, string> VaultErrors = new()
    {
        [1] = "NotInitialized", [2] = "NotAgent", [3] = "NotOwner", [4] = "OverCap",
        [5] = "ValidatorNotAllowed", [6] = "NoSuchProposal", [7] = "ProposalAlreadyResolved",
        [8] = "AlreadyInitialized", [9] = "CapNotLower", [10] = "InsufficientFreeBalance",
        [11] = "Paused", [12] = "PerValidatorCapExceeded", [13] = "RateLimited",
        [14] = "NotInstaller", [15] = "ExceedsCommitted", [16] = "CapNotHigher",
        [17] = "UnauthorizedBondDeposit", [18] = "AgentOwnerSame",
    };

    // The framework codes we've actually hit on testnet — labeled so they're never
    // mistaken for a leash rejection.
    private static readonly Dictionary<int, string> FrameworkErrors = new()
    {
        [64658] = "Odra:MissingArg",
    };

    // Native Casper auction reverts (not Odra user errors) — surface a plain-English reason so an
    // owner doesn't see a raw "ApiError::AuctionError(DelegatorNotFound) [64520]" after a recall.
    private static readonly Dictionary<string, string> AuctionErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DelegatorNotFound"] = "the vault isn't an active delegator on that validator yet — a recent redelegation may still be settling on-chain (~7 eras)",
        ["ValidatorNotFound"] = "that validator isn't in the current auction set",
        ["DelegationAmountTooSmall"] = "below the validator's minimum delegation amount",
        ["DelegationAmountTooLarge"] = "above the validator's maximum delegation amount",
        ["BondNotFound"] = "no matching bond exists on-chain to act on",
    };

    [GeneratedRegex(@"User error:\s*(\d+)")]
    private static partial Regex UserError();

    [GeneratedRegex(@"AuctionError\((\w+)\)")]
    private static partial Regex AuctionError();

    /// Turn a raw chain revert message into something readable. Odra USER errors get their named
    /// code appended ("User error: 4" → "User error: 4 (OverCap)"); native auction reverts get a
    /// plain-English reason ("…AuctionError(DelegatorNotFound)…" → the settling explanation).
    /// Unknown codes and non-revert errors pass through untouched.
    public static string? Humanize(string? error)
    {
        if (string.IsNullOrEmpty(error)) return error;
        var a = AuctionError().Match(error);
        if (a.Success && AuctionErrors.TryGetValue(a.Groups[1].Value, out var help))
            return $"{a.Groups[1].Value} — {help}";
        var m = UserError().Match(error);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var code)) return error;
        if (VaultErrors.TryGetValue(code, out var name)) return $"{error} ({name})";
        return FrameworkErrors.TryGetValue(code, out var fw) ? $"{error} ({fw})" : error;
    }
}
