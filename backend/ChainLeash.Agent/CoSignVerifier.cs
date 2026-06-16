using System.Text.Json.Nodes;

namespace ChainLeash.Agent;

/// Pure verification of a serialized owner-signed transaction — no chain, no I/O, so it can be
/// exhaustively unit-tested. Returns null if the tx is a genuine owner action on package `pkg`,
/// else the rejection reason. Every check FAILS CLOSED: a missing/unparseable field is a
/// rejection, never an accept. This is the chain-of-custody for the "a confirmed action == the
/// owner signed it in their own wallet" claim — every guarded entry point is owner-gated on
/// chain, so a SUCCESSFUL one proves the owner signed, and the server never holds the owner key.
public static class CoSignVerifier
{
    /// Verify a co-sign of material proposal `id` (approve_material): entry point + target
    /// package + owner initiator + the exact proposal id.
    public static string? Verify(JsonObject? txRoot, string pkg, uint id, string? ownerPublicKeyHex, string? ownerAccountHash)
    {
        var v1 = txRoot?["Version1"]?.AsObject() ?? txRoot; // accept either the wrapper or the bare Version1
        var fields = v1?["payload"]?["fields"]?.AsObject();
        if (fields is null) return "tx has no Version1 payload (not a TransactionV1)";

        // entry_point/target can be JSON VALUE nodes for other tx kinds (e.g. "Native") —
        // indexing into a value throws, and a throw here must read as "reject", not crash.
        string? entryPoint = null, addr = null;
        try { entryPoint = fields["entry_point"]?["Custom"]?.GetValue<string>(); } catch { /* not a Custom entry point */ }
        try { addr = fields["target"]?["Stored"]?["id"]?["ByPackageHash"]?["addr"]?.GetValue<string>()?.Replace("hash-", ""); }
        catch { /* not a Stored/ByPackageHash target */ }
        if (!string.Equals(entryPoint, "approve_material", StringComparison.OrdinalIgnoreCase))
            return "tx is not an approve_material call";
        if (addr is null || !string.Equals(addr, pkg?.Replace("hash-", ""), StringComparison.OrdinalIgnoreCase))
            return "tx targets a different contract";

        // id arg — require it, parseable, UNIQUE, and equal to the requested id. Duplicate
        // args are rejected outright: the Casper runtime reads the FIRST occurrence, so a
        // last-wins parse here would be a fail-open seam between verifier and chain.
        uint? foundId = null;
        try
        {
            foreach (var pair in fields["args"]?["Named"]?.AsArray() ?? new JsonArray())
            {
                var arr = pair?.AsArray();
                if (arr is { Count: 2 } && arr[0]?.GetValue<string>() == "id" && arr[1]?["parsed"] is JsonNode pv)
                {
                    if (foundId is not null) return "tx has multiple id arguments";
                    foundId = (uint)pv.GetValue<long>();
                }
            }
        }
        catch { return "could not read the proposal id from the tx"; }
        if (foundId is null) return "tx has no id argument";
        if (foundId != id) return "tx approves a different proposal";

        return OwnerInitiatorReason(v1, ownerPublicKeyHex, ownerAccountHash);
    }

    /// Verify an owner-direct action (set_paused / withdraw / owner_undelegate /
    /// owner_redelegate / reject_material): the tx must call exactly `expectedEntryPoint` on
    /// THIS package and be owner-initiated. The args aren't re-checked — every such entry
    /// point is owner-gated on chain, so a successful owner-initiated call IS the authorization;
    /// the only thing the owner could vary (amount/validator) only ever affects their own funds.
    public static string? VerifyEntryPoint(JsonObject? txRoot, string pkg, string expectedEntryPoint,
        string? ownerPublicKeyHex, string? ownerAccountHash)
    {
        var v1 = txRoot?["Version1"]?.AsObject() ?? txRoot;
        var fields = v1?["payload"]?["fields"]?.AsObject();
        if (fields is null) return "tx has no Version1 payload (not a TransactionV1)";

        string? entryPoint = null, addr = null;
        try { entryPoint = fields["entry_point"]?["Custom"]?.GetValue<string>(); } catch { /* not a Custom entry point */ }
        try { addr = fields["target"]?["Stored"]?["id"]?["ByPackageHash"]?["addr"]?.GetValue<string>()?.Replace("hash-", ""); }
        catch { /* not a Stored/ByPackageHash target */ }
        if (string.IsNullOrEmpty(expectedEntryPoint))
            return "no expected entry point"; // misconfiguration — fail closed
        if (!string.Equals(entryPoint, expectedEntryPoint, StringComparison.OrdinalIgnoreCase))
            return $"tx is not the expected {expectedEntryPoint} call";
        if (addr is null || !string.Equals(addr, pkg?.Replace("hash-", ""), StringComparison.OrdinalIgnoreCase))
            return "tx targets a different contract";

        return OwnerInitiatorReason(v1, ownerPublicKeyHex, ownerAccountHash);
    }

    /// When the owner identity is known, require the tx be owner-initiated (PublicKey or
    /// AccountHash form). Returns null when it matches (or when no owner is configured to
    /// check against), else the rejection reason. Fail-closed: an unparseable initiator is
    /// "not the owner".
    private static string? OwnerInitiatorReason(JsonObject? v1, string? ownerPublicKeyHex, string? ownerAccountHash)
    {
        if (string.IsNullOrEmpty(ownerPublicKeyHex) && string.IsNullOrEmpty(ownerAccountHash)) return null;
        static string Norm(string? s) => (s ?? "").Replace("account-hash-", "").ToLowerInvariant();
        var initPk = v1?["payload"]?["initiator_addr"]?["PublicKey"]?.GetValue<string>();
        var initAh = v1?["payload"]?["initiator_addr"]?["AccountHash"]?.GetValue<string>();
        var pkOk = initPk is not null && !string.IsNullOrEmpty(ownerPublicKeyHex)
                   && string.Equals(initPk, ownerPublicKeyHex, StringComparison.OrdinalIgnoreCase);
        var ahOk = initAh is not null && !string.IsNullOrEmpty(ownerAccountHash) && Norm(initAh) == Norm(ownerAccountHash);
        if (!pkOk && !ahOk) return "tx was not initiated by the owner";
        return null;
    }
}
