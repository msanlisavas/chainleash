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

    [GeneratedRegex(@"User error:\s*(\d+)")]
    private static partial Regex UserError();

    /// Append the named error to a raw chain revert message when it matches one,
    /// e.g. "User error: 4" → "User error: 4 (OverCap)". Unknown codes and
    /// non-revert errors pass through untouched.
    public static string? Humanize(string? error)
    {
        if (string.IsNullOrEmpty(error)) return error;
        var m = UserError().Match(error);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var code)) return error;
        if (VaultErrors.TryGetValue(code, out var name)) return $"{error} ({name})";
        return FrameworkErrors.TryGetValue(code, out var fw) ? $"{error} ({fw})" : error;
    }
}
