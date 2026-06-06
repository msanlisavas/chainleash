using System.Numerics;

namespace ChainLeash.Agent;

/// Decoders for the raw Odra/Casper `bytesrepr` values the agent reads straight out of the
/// GovernedVault's state dictionary (Odra stores Vars as CLValue::Any → raw bytes). Pulled
/// out of ChainReader so the encoding can be unit-tested — a bug here means a wrong cap,
/// bond, or balance shown for the leash.
///   U512 : [len byte][len little-endian value bytes]
///   u32  : 4 little-endian bytes
///   bool : 1 byte (0/1)
public static class OdraBytes
{
    /// Decode a Casper U512 (motes) and return it as whole CSPR.
    public static decimal U512Cspr(byte[]? b)
    {
        if (b is null || b.Length == 0) return 0;
        int n = b[0];
        BigInteger v = 0;
        for (int i = 0; i < n && 1 + i < b.Length; i++) v += (BigInteger)b[1 + i] << (8 * i);
        return (decimal)v / 1_000_000_000m;
    }

    /// Decode a little-endian u32.
    public static uint U32(byte[]? b) =>
        b is null || b.Length < 4 ? 0u : (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

    /// Decode a single-byte bool.
    public static bool Bool(byte[]? b) => b is not null && b.Length > 0 && b[0] == 1;
}
