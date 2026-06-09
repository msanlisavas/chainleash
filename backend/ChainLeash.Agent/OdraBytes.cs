using System.Numerics;

namespace ChainLeash.Agent;

/// Decoders for the raw Odra/Casper `bytesrepr` values the agent reads straight out of the
/// GovernedVault's state dictionary (Odra stores Vars as CLValue::Any → raw bytes). Pulled
/// out of ChainReader so the encoding can be unit-tested — a bug here means a wrong cap,
/// bond, or balance shown for the leash.
///
/// STRICT on purpose: `null` means "field never written" (a legitimate unset Var) and
/// decodes to the type's default, but malformed/truncated bytes THROW — a leash value
/// silently decoded wrong is worse than a loud read failure.
///   U512 : [len byte][len little-endian value bytes]
///   u32  : 4 little-endian bytes      u64 : 8 little-endian bytes
///   bool : 1 byte (0/1)
public static class OdraBytes
{
    /// Decode a Casper U512 (motes) and return it as whole CSPR.
    public static decimal U512Cspr(byte[]? b)
    {
        if (b is null || b.Length == 0) return 0; // unset Var
        int n = b[0];
        if (n > 64 || b.Length != 1 + n)
            throw new FormatException($"malformed U512: declared {n} value bytes, got {b.Length - 1}");
        BigInteger v = 0;
        for (int i = 0; i < n; i++) v += (BigInteger)b[1 + i] << (8 * i);
        return (decimal)v / 1_000_000_000m;
    }

    /// Decode a little-endian u32.
    public static uint U32(byte[]? b)
    {
        if (b is null) return 0; // unset Var
        if (b.Length != 4) throw new FormatException($"malformed u32: expected 4 bytes, got {b.Length}");
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    /// Decode a little-endian u64.
    public static ulong U64(byte[]? b)
    {
        if (b is null) return 0; // unset Var
        if (b.Length != 8) throw new FormatException($"malformed u64: expected 8 bytes, got {b.Length}");
        ulong v = 0;
        for (int i = 0; i < 8; i++) v |= (ulong)b[i] << (8 * i);
        return v;
    }

    /// Decode a single-byte bool. Anything but 0x00/0x01 is corrupt, not "truthy".
    public static bool Bool(byte[]? b)
    {
        if (b is null || b.Length == 0) return false; // unset Var
        if (b.Length != 1 || b[0] > 1) throw new FormatException("malformed bool");
        return b[0] == 1;
    }

    /// A GovernedVault `Proposal` as stored on chain (see governed_vault.rs — odra_type
    /// structs serialize as their fields concatenated in declaration order).
    public sealed record ChainProposal(string ValidatorHex, decimal AmountCspr, bool Undelegate, bool Resolved);

    /// Decode a Proposal struct: validator PublicKey (tag + key bytes), amount U512,
    /// undelegate bool, resolved bool.
    public static ChainProposal Proposal(byte[] b)
    {
        var o = 0;
        if (b.Length < 1) throw new FormatException("empty Proposal bytes");
        byte tag = b[o++];
        var klen = tag switch
        {
            1 => 32, // ed25519
            2 => 33, // secp256k1
            _ => throw new FormatException($"unknown PublicKey tag {tag}"),
        };
        if (b.Length < o + klen + 1) throw new FormatException("truncated Proposal (validator)");
        var validator = $"{tag:x2}{Convert.ToHexString(b, o, klen).ToLowerInvariant()}";
        o += klen;
        int n = b[o++];
        if (n > 64 || b.Length != o + n + 2)
            throw new FormatException("truncated Proposal (amount/flags)");
        BigInteger v = 0;
        for (int i = 0; i < n; i++) v += (BigInteger)b[o + i] << (8 * i);
        o += n;
        if (b[o] > 1 || b[o + 1] > 1) throw new FormatException("malformed Proposal flags");
        return new ChainProposal(validator, (decimal)v / 1_000_000_000m, b[o] == 1, b[o + 1] == 1);
    }
}
