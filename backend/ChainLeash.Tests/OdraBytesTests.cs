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

    // --- malformed input must THROW, never silently decode a wrong leash value ---

    [Fact]
    public void U512Cspr_throws_on_truncated_value()
    {
        // declares 5 value bytes but carries only 3 — the old lenient decode read a
        // smaller number here, which would render a wrong cap as chain-truth
        Assert.Throws<FormatException>(() => OdraBytes.U512Cspr(new byte[] { 5, 0x00, 0x70, 0xC9 }));
    }

    [Fact]
    public void U512Cspr_throws_on_trailing_garbage() =>
        Assert.Throws<FormatException>(() => OdraBytes.U512Cspr(new byte[] { 1, 0x01, 0xFF }));

    [Fact]
    public void U512Cspr_throws_on_impossible_length() =>
        Assert.Throws<FormatException>(() => OdraBytes.U512Cspr(new byte[] { 65 }));

    [Theory]
    [InlineData(new byte[] { 1, 0, 0 })]    // short
    [InlineData(new byte[] { 1, 0, 0, 0, 0 })] // long
    public void U32_throws_on_wrong_length(byte[] bytes) =>
        Assert.Throws<FormatException>(() => OdraBytes.U32(bytes));

    [Fact]
    public void Bool_throws_on_non_canonical_bytes()
    {
        Assert.Throws<FormatException>(() => OdraBytes.Bool(new byte[] { 2 }));
        Assert.Throws<FormatException>(() => OdraBytes.Bool(new byte[] { 1, 0 }));
    }

    [Fact]
    public void U64_decodes_little_endian_and_rejects_wrong_length()
    {
        Assert.Equal(30_000UL, OdraBytes.U64(new byte[] { 0x30, 0x75, 0, 0, 0, 0, 0, 0 }));
        Assert.Equal(0UL, OdraBytes.U64(null));
        Assert.Throws<FormatException>(() => OdraBytes.U64(new byte[] { 1, 2, 3 }));
    }

    // --- Proposal struct (validator PublicKey ++ amount U512 ++ undelegate ++ resolved) ---

    static byte[] ProposalBytes(byte resolved = 1, byte undelegate = 0)
    {
        var key = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        // 5000 CSPR = 5_000_000_000_000 motes = 0x048C27395000 → 6 LE bytes
        var amount = new byte[] { 6, 0x00, 0x50, 0x39, 0x27, 0x8C, 0x04 };
        return new byte[] { 1 }.Concat(key).Concat(amount).Concat(new[] { undelegate, resolved }).ToArray();
    }

    [Fact]
    public void Proposal_decodes_an_ed25519_proposal()
    {
        var p = OdraBytes.Proposal(ProposalBytes(resolved: 0, undelegate: 1));
        Assert.Equal("01" + new string('a', 64), p.ValidatorHex);
        Assert.Equal(5000m, p.AmountCspr);
        Assert.True(p.Undelegate);
        Assert.False(p.Resolved);
    }

    [Fact]
    public void Proposal_throws_on_unknown_key_tag()
    {
        var b = ProposalBytes();
        b[0] = 9;
        Assert.Throws<FormatException>(() => OdraBytes.Proposal(b));
    }

    [Fact]
    public void Proposal_throws_on_truncation()
    {
        var b = ProposalBytes();
        Assert.Throws<FormatException>(() => OdraBytes.Proposal(b.Take(b.Length - 1).ToArray()));
    }
}

/// The audit feed translates raw chain revert codes into the vault's named errors.
/// Odra user errors revert UNSHIFTED (OverCap == `User error: 4`); the 64536+ range
/// belongs to Odra's own framework errors.
public class OdraErrorsTests
{
    [Fact]
    public void Humanize_names_a_vault_revert() =>
        Assert.Equal("User error: 4 (OverCap)", OdraErrors.Humanize("User error: 4"));

    [Fact]
    public void Humanize_names_the_new_codes()
    {
        Assert.Contains("UnauthorizedBondDeposit", OdraErrors.Humanize("User error: 17"));
        Assert.Contains("AgentOwnerSame", OdraErrors.Humanize("User error: 18"));
    }

    [Fact]
    public void Humanize_labels_known_framework_errors_distinctly() =>
        Assert.Equal("User error: 64658 (Odra:MissingArg)", OdraErrors.Humanize("User error: 64658"));

    [Theory]
    [InlineData("User error: 64540")]          // framework range, not a vault code — never mislabel
    [InlineData("User error: 60000")]          // not a discriminant we know
    [InlineData("Out of gas error")]           // not a user revert at all
    [InlineData("ApiError::AuctionError(SomeUnknownThing) [9]")] // auction error we don't have help for
    [InlineData(null)]
    public void Humanize_passes_everything_else_through(string? raw) =>
        Assert.Equal(raw, OdraErrors.Humanize(raw));

    [Fact]
    public void Humanize_explains_known_auction_reverts_in_plain_english()
    {
        var r = OdraErrors.Humanize("ApiError::AuctionError(DelegatorNotFound) [64520]");
        Assert.Contains("DelegatorNotFound", r);
        Assert.Contains("settling", r);          // the redelegate-not-settled explanation
        Assert.DoesNotContain("64520", r);       // raw code is replaced, not appended
        Assert.Contains("minimum delegation", OdraErrors.Humanize("ApiError::AuctionError(DelegationAmountTooSmall) [64557]"));
    }
}

/// Replay-protection set: bounded, atomic claims, and release must not leave stale
/// eviction entries that could prematurely evict a re-claimed key.
public class BoundedSetTests
{
    [Fact]
    public void Claims_are_atomic_and_single_use()
    {
        var s = new BoundedSet(4);
        Assert.True(s.TryAdd("a"));
        Assert.False(s.TryAdd("a"));
    }

    [Fact]
    public void Eviction_drops_the_oldest_once_full()
    {
        var s = new BoundedSet(2);
        s.TryAdd("a"); s.TryAdd("b"); s.TryAdd("c"); // evicts a
        Assert.True(s.TryAdd("a"));  // a was evicted → claimable again
        Assert.False(s.TryAdd("c")); // c is still held
    }

    [Fact]
    public void Release_then_reclaim_is_not_prematurely_evicted()
    {
        var s = new BoundedSet(2);
        s.TryAdd("a");
        s.Remove("a");               // released — must leave no stale eviction entry
        s.TryAdd("a");               // re-claimed
        s.TryAdd("b");               // fills to capacity; nothing should evict "a" early
        Assert.False(s.TryAdd("a")); // the re-claim is still held
    }
}
