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
    [InlineData(600, 600, false, false)] // EXACTLY at the cap → routine (the chain's OverCap is strict >, too)
    [InlineData(600, 600, true, true)]   // at the cap but elevated risk → still co-signed
    public void RequiresProposal_when_over_cap_or_elevated_risk(
        decimal amount, decimal cap, bool elevated, bool expected) =>
        Assert.Equal(expected, StakingPolicy.RequiresProposal(amount, cap, elevated));

    [Theory]
    [InlineData(700, 600, true)]   // can't unwind > cap in one routine tx → escalate
    [InlineData(600, 600, false)]  // exactly at the cap → routine exit
    [InlineData(300, 600, false)]  // under the cap → routine exit
    public void MustEscalateExit_only_above_the_cap(decimal position, decimal cap, bool expected) =>
        Assert.Equal(expected, StakingPolicy.MustEscalateExit(position, cap));

    [Theory]
    // Opening a NEW position (no existing stake): must exceed the network minimum, else the auction
    // rejects it as DelegationAmountTooSmall and the stake strands as phantom committed (0147c).
    [InlineData(500, 0, 500, false)]    // 500 into a fresh validator, min 500 → would NOT bond (the incident)
    [InlineData(501, 0, 500, true)]     // just over the minimum → bonds
    [InlineData(1000, 0, 500, true)]    // comfortably over → bonds
    [InlineData(400, 0, 500, false)]    // below the minimum → would NOT bond
    // Topping up an EXISTING position always clears the minimum, regardless of the added amount.
    [InlineData(500, 6000, 500, true)]  // add 500 to an existing 6000 position → bonds
    [InlineData(1, 500, 500, true)]     // even a tiny top-up of an existing position → bonds
    public void BondWouldStick_guards_new_positions_at_the_network_minimum(
        decimal amount, decimal existingPosition, decimal minDelegation, bool expected) =>
        Assert.Equal(expected, StakingPolicy.BondWouldStick(amount, existingPosition, minDelegation));

    [Fact]
    public void CanAgentAutoExit_only_for_active_validators()
    {
        Assert.True(StakingPolicy.CanAgentAutoExit(true));
        Assert.False(StakingPolicy.CanAgentAutoExit(false)); // inactive → hand to owner, never auto-escalate a doomed exit
    }

    [Theory]
    // an unresolved exit proposal targets this validator: active vs left-the-auction
    [InlineData(true, true, 6000, 0, 500, "Exit proposed — awaiting owner co-sign")]
    [InlineData(true, false, 6000, 0, 500, "Exit proposed, but validator left the auction — reject it (can't co-sign)")]
    // directed principal but no current on-chain stake
    [InlineData(false, false, 6000, 0, 500, "Validator left the auction — clear stale committed")]
    [InlineData(false, true, 400, 0, 500, "Unbonded — below the network minimum; needs owner action")]
    [InlineData(false, true, 6000, 0, 500, "Settling (~7 eras)")]
    // other states
    [InlineData(false, true, 0, 500, 500, "Unbonding")]
    [InlineData(false, true, 6000, 6000, 500, "Delegated")]
    public void PositionStatus_maps_states(bool exiting, bool active, decimal principal, decimal current, decimal min, string expected) =>
        Assert.Equal(expected, StakingPolicy.PositionStatus(exiting, active, principal, current, min));
}
