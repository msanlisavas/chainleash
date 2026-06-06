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
                         string? initiatorPk = Owner, string? initiatorAh = null)
    {
        var args = idArg is null ? "[]" : $"[[\"id\",{{\"cl_type\":\"U32\",\"parsed\":{idArg}}}]]";
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
}
