namespace ChainLeash.Agent;

/// Casper staking rewards auto-compound INTO the delegation, so a position's accrued reward
/// is its current on-chain stake minus the principal the vault directed (the contract's
/// `committed`). Clamped at 0: a position that is unbonding (committed already reduced while
/// the stake settles) must never read as negative, and a no-principal position isn't "earning".
public static class RewardMath
{
    public static decimal RewardCspr(decimal currentStakeCspr, decimal principalCspr) =>
        principalCspr > 0m && currentStakeCspr > principalCspr ? currentStakeCspr - principalCspr : 0m;
}
