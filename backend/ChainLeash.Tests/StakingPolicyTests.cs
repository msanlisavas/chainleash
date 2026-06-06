using ChainLeash.Agent;
using Xunit;

namespace ChainLeash.Tests;

/// The leash's decision core, exhaustively. These are the rules the chain also enforces;
/// the agent must never propose a move that violates them.
public class StakingPolicyTests
{
    [Theory]
    [InlineData(5, true, 6, true)]    // active, under cap → compliant
    [InlineData(6, true, 6, true)]    // exactly at the cap → compliant (≤)
    [InlineData(7, true, 6, false)]   // over the commission cap → not compliant
    [InlineData(0, false, 6, false)]  // inactive (evicted) → never compliant, even at 0%
    public void IsCompliant_enforces_active_and_commission_cap(int fee, bool active, int max, bool expected) =>
        Assert.Equal(expected, StakingPolicy.IsCompliant(fee, active, max));

    [Fact]
    public void ComplianceNote_explains_each_verdict()
    {
        Assert.Contains("inactive", StakingPolicy.ComplianceNote(3, false, 6));
        Assert.Contains("> policy", StakingPolicy.ComplianceNote(9, true, 6));
        Assert.Contains("≤ policy", StakingPolicy.ComplianceNote(3, true, 6));
    }

    [Theory]
    [InlineData(500, 500, true, true, true)]    // exactly a chunk free, compliant best → deploy
    [InlineData(1000, 500, true, true, true)]   // plenty free → deploy
    [InlineData(499, 500, true, true, false)]   // less than a chunk free → hold
    [InlineData(1000, 500, false, false, false)]// no best validator → hold
    [InlineData(1000, 500, true, false, false)] // best exists but not compliant → hold
    public void CanDeploy_requires_a_chunk_free_and_a_compliant_best(
        decimal free, decimal chunk, bool bestExists, bool bestCompliant, bool expected) =>
        Assert.Equal(expected, StakingPolicy.CanDeploy(free, chunk, bestExists, bestCompliant));

    [Theory]
    [InlineData(1000, 500, 500)] // free > chunk → deploy a chunk
    [InlineData(300, 500, 300)]  // free < chunk → deploy only what's free
    public void DeployAmount_is_a_chunk_but_never_more_than_free(decimal free, decimal chunk, decimal expected) =>
        Assert.Equal(expected, StakingPolicy.DeployAmount(free, chunk));

    [Theory]
    [InlineData(800, 600, 600)] // position over cap → pull only a cap's worth per action
    [InlineData(400, 600, 400)] // position under cap → pull the whole position
    public void ExitAmount_is_the_position_capped_per_action(decimal position, decimal cap, decimal expected) =>
        Assert.Equal(expected, StakingPolicy.ExitAmount(position, cap));

    [Theory]
    [InlineData(700, 600, false, true)]  // over the cap → must be co-signed
    [InlineData(500, 600, true, true)]   // elevated risk read → must be co-signed
    [InlineData(500, 600, false, false)] // within cap, normal risk → routine, no co-sign
    public void RequiresProposal_when_over_cap_or_elevated_risk(
        decimal amount, decimal cap, bool elevated, bool expected) =>
        Assert.Equal(expected, StakingPolicy.RequiresProposal(amount, cap, elevated));

    [Theory]
    [InlineData(700, 600, true)]   // can't unwind > cap in one routine tx → escalate
    [InlineData(600, 600, false)]  // exactly at the cap → routine exit
    [InlineData(300, 600, false)]  // under the cap → routine exit
    public void MustEscalateExit_only_above_the_cap(decimal position, decimal cap, bool expected) =>
        Assert.Equal(expected, StakingPolicy.MustEscalateExit(position, cap));
}
