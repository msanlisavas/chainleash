using ChainLeash.Agent;
using Xunit;

namespace ChainLeash.Tests;

/// The agent reads the leash's cap/bond/balances straight from the vault's raw Odra
/// bytesrepr. A decode bug here = a wrong number shown for the leash, so pin the encoding.
public class OdraBytesTests
{
    [Fact]
    public void U512Cspr_decodes_one_cspr()
    {
        // 1 CSPR = 1_000_000_000 motes = 0x3B9ACA00 → 4 little-endian bytes after the len byte.
        var bytes = new byte[] { 4, 0x00, 0xCA, 0x9A, 0x3B };
        Assert.Equal(1m, OdraBytes.U512Cspr(bytes));
    }

    [Fact]
    public void U512Cspr_decodes_a_large_balance()
    {
        // 600 CSPR = 600_000_000_000 motes = 0x8BB2C97000 → 5 bytes.
        var bytes = new byte[] { 5, 0x00, 0x70, 0xC9, 0xB2, 0x8B };
        Assert.Equal(600m, OdraBytes.U512Cspr(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void U512Cspr_treats_unset_as_zero(byte[]? bytes) => Assert.Equal(0m, OdraBytes.U512Cspr(bytes));

    [Fact]
    public void U32_decodes_little_endian()
    {
        Assert.Equal(1u, OdraBytes.U32(new byte[] { 1, 0, 0, 0 }));
        Assert.Equal(258u, OdraBytes.U32(new byte[] { 2, 1, 0, 0 })); // 0x0102
        Assert.Equal(0u, OdraBytes.U32(null));
    }

    [Fact]
    public void Bool_decodes_single_byte()
    {
        Assert.True(OdraBytes.Bool(new byte[] { 1 }));
        Assert.False(OdraBytes.Bool(new byte[] { 0 }));
        Assert.False(OdraBytes.Bool(null));
    }
}
