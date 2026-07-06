namespace ChainLeash.Agent;

/// The pure, deterministic core of the leash — no I/O, no chain, no clock. Both the
/// perception layer (ValidatorMonitor) and the agent loop (AgentWorker) delegate their
/// decisions here so the leash logic can be unit-tested in isolation. Institutions want
/// a published rule executed auditably, not opaque discretion — so it lives in one place.
public static class StakingPolicy
{
    /// A validator satisfies policy iff it is active AND its commission is within the cap.
    public static bool IsCompliant(int feePercent, bool isActive, int maxCommissionPercent) =>
        isActive && feePercent <= maxCommissionPercent;

    /// Human-readable reason shown in the audit feed / dashboard for a validator's verdict.
    public static string ComplianceNote(int feePercent, bool isActive, int maxCommissionPercent) =>
        !isActive ? "inactive (evicted/unbonded)"
        : feePercent > maxCommissionPercent ? $"commission {feePercent}% > policy {maxCommissionPercent}%"
        : $"commission {feePercent}% ≤ policy {maxCommissionPercent}%";

    /// Idle treasury can be deployed only if there's at least a chunk free AND a compliant
    /// best validator to deploy it into.
    public static bool CanDeploy(decimal free, decimal chunk, bool bestExists, bool bestCompliant) =>
        free >= chunk && bestExists && bestCompliant;

    /// How much idle CSPR to deploy in one routine move: a chunk, but never more than is free.
    public static decimal DeployAmount(decimal free, decimal chunk) => Math.Min(free, chunk);

    /// How much to pull when exiting a breaching validator: its position, capped per action.
    public static decimal ExitAmount(decimal position, decimal cap) => Math.Min(position, cap);

    /// Whether a delegate/redelegate of `amount` will actually BOND. Casper rejects a delegation
    /// that would OPEN a new position at or below the network minimum delegation amount
    /// (AuctionError::DelegationAmountTooSmall) — but topping up a position the vault already holds
    /// always clears the minimum. Gating on this stops the agent stranding a chunk as phantom
    /// `committed` (the 0147c incident: a 500-CSPR redelegate into a fresh validator, min = 500).
    public static bool BondWouldStick(decimal amount, decimal existingPosition, decimal minDelegation) =>
        existingPosition > 0m || amount > minDelegation;

    /// A move is "material" — needs human co-sign — if it exceeds the per-action cap OR the
    /// paid risk read came back elevated.
    public static bool RequiresProposal(decimal amount, decimal cap, bool elevatedRisk) =>
        amount > cap || elevatedRisk;

    /// Exiting a breaching validator must be escalated to a co-signed proposal when the
    /// position to unwind exceeds the per-action cap (can't be done in one routine tx).
    public static bool MustEscalateExit(decimal position, decimal cap) => position > cap;

    /// The agent may auto-exit a breaching validator only if its bid still exists in the auction.
    /// An inactive validator may have WITHDRAWN its bid, so any undelegate/redelegate reverts
    /// ValidatorNotFound — those are handed to the owner (recall / clear-committed), never auto-escalated.
    public static bool CanAgentAutoExit(bool validatorActive) => validatorActive;

    /// The dashboard status for one directed position. `exiting` = an unresolved owner-exit proposal
    /// targets this validator; `validatorActive` = it is still in the era set. A validator that has
    /// LEFT the auction can neither be co-signed (undelegate reverts) nor is it "settling" — label
    /// those honestly and point the owner at reject / clear-committed.
    public static string PositionStatus(bool exiting, bool validatorActive, decimal principal, decimal current, decimal minDelegation)
    {
        if (exiting)
            return validatorActive
                ? "Exit proposed — awaiting owner co-sign"
                : "Exit proposed, but validator left the auction — reject it (can't co-sign)";
        if (principal > 0m && current <= 0m)
        {
            if (!validatorActive)
                return "Validator left the auction — clear stale committed";
            return principal <= minDelegation
                ? "Unbonded — below the network minimum; needs owner action"
                : "Settling (~7 eras)";
        }
        if (principal <= 0m && current > 0m) return "Unbonding";
        return "Delegated";
    }
}
