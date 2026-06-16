using System.Text.Json.Nodes;
using ChainLeash.Agent;
using Xunit;

namespace ChainLeash.Tests;

/// The forge-rejection logic for the in-wallet co-sign — the chain-of-custody for
/// "a confirmed co-sign == the owner signed it in their own wallet". Must fail CLOSED.
public class CoSignVerifierTests
{
    const string Pkg = "716d71bded4901c66169e7b4b207f043a75f6ffe1fc037ab9e25d89425b25aa0";
    const string Owner = "011a493f54cadec630682aa2c64a40d0569b9c3a924876c010cfe27735162654b5";
    const string OwnerHash = "5bc1cf012c678676ff14c3cd3d2d72ac19d17819d448de4795f7bf1618bfd232";

    // Build the C#-SDK Transaction serialization shape: {"Version1":{payload:{...}}}.
    static JsonObject Tx(string entryPoint = "approve_material", string? pkg = Pkg, string? idArg = "0",
                         string? initiatorPk = Owner, string? initiatorAh = null, string? argsOverride = null)
    {
        var args = argsOverride ?? (idArg is null ? "[]" : $"[[\"id\",{{\"cl_type\":\"U32\",\"parsed\":{idArg}}}]]");
        var initiator = initiatorAh is not null
            ? $"{{\"AccountHash\":\"account-hash-{initiatorAh}\"}}"
            : $"{{\"PublicKey\":\"{initiatorPk}\"}}";
        var target = pkg is null ? "\"Native\""
            : $"{{\"Stored\":{{\"id\":{{\"ByPackageHash\":{{\"addr\":\"{pkg}\",\"version\":null}}}},\"runtime\":\"VmCasperV1\"}}}}";
        var json = $@"{{""Version1"":{{""hash"":""abc"",""payload"":{{
            ""initiator_addr"":{initiator},
            ""fields"":{{""args"":{{""Named"":{args}}},""entry_point"":{{""Custom"":""{entryPoint}""}},""target"":{target}}}
        }}}}}}";
        return JsonNode.Parse(json)!.AsObject();
    }

    [Fact]
    public void Valid_owner_cosign_passes() =>
        Assert.Null(CoSignVerifier.Verify(Tx(), Pkg, 0, Owner, OwnerHash));

    [Fact]
    public void Wrong_entry_point_rejected() =>
        Assert.Contains("approve_material", CoSignVerifier.Verify(Tx(entryPoint: "delegate"), Pkg, 0, Owner, OwnerHash)!);

    [Fact]
    public void Wrong_target_package_rejected() =>
        Assert.Contains("different contract", CoSignVerifier.Verify(Tx(pkg: "00".PadLeft(64, '0')), Pkg, 0, Owner, OwnerHash)!);

    [Fact]
    public void Wrong_id_rejected() =>
        Assert.Contains("different proposal", CoSignVerifier.Verify(Tx(idArg: "5"), Pkg, 0, Owner, OwnerHash)!);

    [Fact]
    public void Missing_id_fails_closed() =>
        Assert.Contains("no id argument", CoSignVerifier.Verify(Tx(idArg: null), Pkg, 0, Owner, OwnerHash)!);

    [Fact]
    public void Non_owner_initiator_rejected() =>
        Assert.Contains("not initiated by the owner",
            CoSignVerifier.Verify(Tx(initiatorPk: "01" + new string('a', 64)), Pkg, 0, Owner, OwnerHash)!);

    [Fact]
    public void AccountHash_initiator_matching_owner_passes() =>
        Assert.Null(CoSignVerifier.Verify(Tx(initiatorPk: null, initiatorAh: OwnerHash), Pkg, 0, Owner, OwnerHash));

    [Fact]
    public void No_owner_configured_skips_initiator_check() =>
        Assert.Null(CoSignVerifier.Verify(Tx(initiatorPk: "01" + new string('a', 64)), Pkg, 0, null, null));

    [Fact]
    public void Malformed_tx_fails_closed() =>
        Assert.NotNull(CoSignVerifier.Verify(JsonNode.Parse("{\"nope\":1}")!.AsObject(), Pkg, 0, Owner, OwnerHash));

    [Fact]
    public void Duplicate_id_args_rejected()
    {
        // The Casper runtime reads the FIRST named arg; a last-wins parse would let
        // [id=0, id=5] verify as 0 while the chain executes 5. Must reject outright.
        var dup = "[[\"id\",{\"cl_type\":\"U32\",\"parsed\":0}],[\"id\",{\"cl_type\":\"U32\",\"parsed\":5}]]";
        Assert.Contains("multiple id", CoSignVerifier.Verify(Tx(argsOverride: dup), Pkg, 0, Owner, OwnerHash)!);
    }

    [Fact]
    public void Bare_version1_payload_accepted()
    {
        var bare = Tx()["Version1"]!.DeepClone().AsObject(); // some wallets return the node unwrapped
        Assert.Null(CoSignVerifier.Verify(bare, Pkg, 0, Owner, OwnerHash));
    }

    [Fact]
    public void Hash_prefixed_target_addr_normalized() =>
        Assert.Null(CoSignVerifier.Verify(Tx(pkg: "hash-" + Pkg), Pkg, 0, Owner, OwnerHash));

    [Fact]
    public void Native_target_rejected() =>
        Assert.Contains("different contract", CoSignVerifier.Verify(Tx(pkg: null), Pkg, 0, Owner, OwnerHash)!);

    [Fact]
    public void Non_numeric_id_fails_closed() =>
        Assert.NotNull(CoSignVerifier.Verify(
            Tx(argsOverride: "[[\"id\",{\"cl_type\":\"U32\",\"parsed\":\"zero\"}]]"), Pkg, 0, Owner, OwnerHash));

    // --- VerifyEntryPoint: the owner-direct controls (pause/withdraw/owner_undelegate/…).
    // Same fail-closed chain-of-custody as the material co-sign, minus the proposal-id check. ---

    [Fact]
    public void Owner_action_valid_passes() =>
        Assert.Null(CoSignVerifier.VerifyEntryPoint(Tx(entryPoint: "set_paused"), Pkg, "set_paused", Owner, OwnerHash));

    [Fact]
    public void Owner_action_wrong_entry_point_rejected() =>
        Assert.Contains("expected owner_undelegate",
            CoSignVerifier.VerifyEntryPoint(Tx(entryPoint: "set_paused"), Pkg, "owner_undelegate", Owner, OwnerHash)!);

    [Fact]
    public void Owner_action_wrong_package_rejected() =>
        Assert.Contains("different contract",
            CoSignVerifier.VerifyEntryPoint(Tx(entryPoint: "withdraw", pkg: new string('0', 64)), Pkg, "withdraw", Owner, OwnerHash)!);

    [Fact]
    public void Owner_action_non_owner_initiator_rejected() =>
        Assert.Contains("not initiated by the owner",
            CoSignVerifier.VerifyEntryPoint(Tx(entryPoint: "withdraw", initiatorPk: "01" + new string('a', 64)), Pkg, "withdraw", Owner, OwnerHash)!);

    [Fact]
    public void Owner_action_accounthash_owner_passes() =>
        Assert.Null(CoSignVerifier.VerifyEntryPoint(
            Tx(entryPoint: "owner_undelegate", initiatorPk: null, initiatorAh: OwnerHash), Pkg, "owner_undelegate", Owner, OwnerHash));

    [Fact]
    public void Owner_action_no_owner_configured_skips_initiator() =>
        Assert.Null(CoSignVerifier.VerifyEntryPoint(
            Tx(entryPoint: "set_paused", initiatorPk: "01" + new string('a', 64)), Pkg, "set_paused", null, null));

    [Fact]
    public void Owner_action_native_target_rejected() =>
        Assert.Contains("different contract",
            CoSignVerifier.VerifyEntryPoint(Tx(entryPoint: "withdraw", pkg: null), Pkg, "withdraw", Owner, OwnerHash)!);

    [Fact]
    public void Owner_action_empty_expected_entry_point_fails_closed() =>
        Assert.NotNull(CoSignVerifier.VerifyEntryPoint(Tx(entryPoint: "set_paused"), Pkg, "", Owner, OwnerHash));

    [Fact]
    public void Owner_action_malformed_tx_fails_closed() =>
        Assert.NotNull(CoSignVerifier.VerifyEntryPoint(JsonNode.Parse("{\"nope\":1}")!.AsObject(), Pkg, "set_paused", Owner, OwnerHash));

    [Fact]
    public void Owner_action_hash_prefixed_addr_normalized() =>
        Assert.Null(CoSignVerifier.VerifyEntryPoint(Tx(entryPoint: "withdraw", pkg: "hash-" + Pkg), Pkg, "withdraw", Owner, OwnerHash));
}
