using ChainLeash.Agent;
using Xunit;

namespace ChainLeash.Tests;

public class StakingPositionsTests
{
    [Fact]
    public void UrefAddress_strips_prefix_and_suffix_lowercased()
    {
        Assert.Equal("f961b1821e", StakingPositions.UrefAddress("uref-F961B1821E-007"));
        Assert.Equal("", StakingPositions.UrefAddress(null));
        Assert.Equal("", StakingPositions.UrefAddress("   "));
    }

    [Fact]
    public void VaultStakeCspr_matches_the_vault_uref_row_suffix_insensitive()
    {
        var json = """
        {"data":[
          {"delegator_identifier":"01aa","delegator_identifier_type_id":0,"stake":"1000000000"},
          {"delegator_identifier":"uref-f961b1821e1261cbc91c763b1fac8037725e343b558954918936e5f9700276fe-007","delegator_identifier_type_id":1,"stake":"2002420000000"}
        ]}
        """;
        // queried with a DIFFERENT access-rights suffix — must still match on the address
        var addr = StakingPositions.UrefAddress("uref-f961b1821e1261cbc91c763b1fac8037725e343b558954918936e5f9700276fe-000");
        Assert.Equal(2002.42m, StakingPositions.VaultStakeCspr(json, addr));
    }

    [Fact]
    public void VaultStakeCspr_returns_zero_when_vault_absent_or_empty()
    {
        Assert.Equal(0m, StakingPositions.VaultStakeCspr("""{"data":[{"delegator_identifier":"01aa","delegator_identifier_type_id":0,"stake":"1000000000"}]}""", "deadbeef"));
        Assert.Equal(0m, StakingPositions.VaultStakeCspr("""{"data":[]}""", "deadbeef"));
    }

    [Theory]
    [InlineData(2002.42, 2000, 2.42)]
    [InlineData(500, 500, 0)]    // delegated, no reward yet
    [InlineData(0, 0, 0)]        // empty
    [InlineData(900, 1000, 0)]   // settling/unbonding: never negative
    [InlineData(100, 0, 0)]      // no principal → not "earning"
    public void RewardCspr_is_current_minus_principal_clamped(decimal current, decimal principal, decimal expected)
        => Assert.Equal(expected, RewardMath.RewardCspr(current, principal));
}
