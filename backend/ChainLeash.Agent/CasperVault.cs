using System.Text.Json;
using System.Text.Json.Nodes;
using Casper.Network.SDK;
using Casper.Network.SDK.Types;

namespace ChainLeash.Agent;

/// On-chain interface to the live staking GovernedVault (Casper 2.0 testnet).
/// The vault HOLDS the treasury's CSPR and delegates it; this client issues the
/// agent's routine moves (delegate/undelegate, capped + allowlisted) and over-cap
/// "material" proposals for the human to co-sign. Calls go by package hash, signed
/// by the agent key. The chain enforces the cap/allowlist — see governed_vault.rs.
public sealed class CasperVault
{
    private readonly IConfiguration _cfg;
    private readonly KeyPair? _agentKp;
    private readonly string _pkg;
    private readonly PublicKey? _ownerKey;

    public CasperVault(IConfiguration cfg)
    {
        _cfg = cfg;
        // The agent key is OPTIONAL: with no key the agent runs OBSERVER-ONLY — it reads
        // the live vault (gas-free) and the dashboard/health serve, but it signs nothing.
        // This lets a fresh clone boot cleanly instead of crash-looping with no secrets.
        var agentPath = cfg["Casper:AgentSecretKeyPath"];
        if (!string.IsNullOrWhiteSpace(agentPath) && File.Exists(agentPath))
            _agentKp = KeyPair.FromPem(agentPath);
        _pkg = cfg["Casper:GovernedVaultPackageHash"]!.Replace("hash-", "");
        var ownerHex = cfg["Casper:OwnerPublicKeyHex"];
        if (!string.IsNullOrWhiteSpace(ownerHex) && !ownerHex.StartsWith("<"))
            _ownerKey = PublicKey.FromHexString(ownerHex);
    }

    /// The agent's signing key, or null in observer mode (no key configured).
    public PublicKey? AgentKey => _agentKp?.PublicKey;

    /// True when no agent key is configured — the agent observes but cannot act on-chain.
    public bool ReadOnly => _agentKp is null;

    /// The owner (human / institution) account that co-signs material moves. The server
    /// holds only this PUBLIC key — the secret never leaves the owner's wallet.
    public PublicKey? OwnerKey => _ownerKey;

    /// Whether the legacy server-key co-sign fallback is enabled (dev only). Default OFF:
    /// in production the owner co-signs in their own wallet, so the server holds no owner key.
    public bool AllowServerKeyCoSign => _cfg.GetValue("Casper:AllowServerKeyCoSign", false);

    /// Routine autonomous delegation (≤ cap, validator must be allowlisted).
    /// The chain reverts with OverCap/ValidatorNotAllowed if the leash is exceeded.
    public Task<TxResult> Delegate(PublicKey validator, ulong amountMotes) =>
        Call("delegate", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        }, paymentMotes: 30_000_000_000UL);

    /// Routine autonomous undelegation (≤ cap). Funds unbond back to the VAULT,
    /// never to the agent — the agent can rebalance but can't drain.
    public Task<TxResult> Undelegate(PublicKey validator, ulong amountMotes) =>
        Call("undelegate", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        }, paymentMotes: 30_000_000_000UL);

    /// Routine autonomous redelegation — move stake from one validator to another in a
    /// single native tx (≤ cap, destination allowlisted). The cleanest "switch to a
    /// better validator" move: no manual unstake/restake, destination committed on-chain.
    public Task<TxResult> Redelegate(PublicKey from, PublicKey to, ulong amountMotes) =>
        Call("redelegate", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(from)),
            new NamedArg("new_validator", CLValue.PublicKey(to)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        }, paymentMotes: 30_000_000_000UL);

    /// Agent proposes an over-cap (material) (un)delegation — emits MaterialProposed
    /// on-chain and waits for the human owner to co-sign approve_material.
    public Task<TxResult> ProposeMaterial(PublicKey validator, ulong amountMotes, bool undelegate) =>
        Call("propose_material", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
            new NamedArg("undelegate", CLValue.Bool(undelegate)),
        });

    /// Post the agent's slashable bond into the vault (payable, via Odra's proxy_caller
    /// session — same mechanism as treasury deposits). Skin in the game, on-chain.
    public async Task<TxResult> PostBond(ulong amountMotes)
    {
        if (_agentKp is null) return new TxResult("", false, "read-only: no agent key configured (observer mode)");
        var proxyPath = _cfg["Casper:ProxyCallerWasmPath"] ?? "../../contracts/governed_vault/wasm/proxy_caller.wasm";
        if (!File.Exists(proxyPath)) return new TxResult("", false, $"proxy wasm not found: {proxyPath}");
        var proxy = File.ReadAllBytes(proxyPath);
        var innerArgs = CLValue.List(new[] { CLValue.U8(0), CLValue.U8(0), CLValue.U8(0), CLValue.U8(0) });
        var rargs = new List<NamedArg>
        {
            new NamedArg("package_hash", CLValue.ByteArray(Convert.FromHexString(_pkg))),
            new NamedArg("entry_point", CLValue.String("deposit_bond")),
            new NamedArg("args", innerArgs),
            new NamedArg("attached_value", CLValue.U512(amountMotes)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        };
        var tx = new Transaction.SessionBuilder()
            .From(_agentKp.PublicKey).Wasm(proxy).RuntimeArgs(rargs)
            .ChainName(_cfg["Casper:ChainName"]!).Payment(20_000_000_000UL, 1).Build();
        tx.Sign(_agentKp);
        var client = Rpc();
        await client.PutTransaction(tx);
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(5000);
            var er = (await client.GetTransaction(tx.Hash, CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null) return new TxResult(tx.Hash, er.IsSuccess, er.IsSuccess ? null : er.ErrorMessage);
        }
        return new TxResult(tx.Hash, false, "timeout");
    }

    /// Overseer tightens (only lowers) the cap — emits CapTightened.
    public Task<TxResult> TightenCap(ulong newCapMotes) =>
        Call("tighten_cap", new List<NamedArg> { new NamedArg("new_cap", CLValue.U512(newCapMotes)) });

    /// Owner co-signs and executes a pending material proposal. Signed by the HUMAN
    /// key (the dashboard "Co-sign" action). In production this signature comes from
    /// the owner's wallet via CSPR.click rather than a server-held key.
    public async Task<TxResult> ApproveMaterial(uint id)
    {
        var humanPath = _cfg["Casper:HumanSecretKeyPath"];
        if (string.IsNullOrWhiteSpace(humanPath)) return new TxResult("", false, "no human key configured");
        var humanKp = KeyPair.FromPem(humanPath);

        var tx = new Transaction.ContractCallBuilder()
            .ByPackageHash(_pkg, null, null)
            .EntryPoint("approve_material")
            .RuntimeArgs(new List<NamedArg> { new NamedArg("id", CLValue.U32(id)) })
            .From(humanKp.PublicKey)
            .ChainName(_cfg["Casper:ChainName"]!)
            .Payment(30_000_000_000UL, 1)
            .Build();
        tx.Sign(humanKp);

        var client = Rpc();
        await client.PutTransaction(tx);
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(5000);
            var er = (await client.GetTransaction(tx.Hash, CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null) return new TxResult(tx.Hash, er.IsSuccess, er.IsSuccess ? null : er.ErrorMessage);
        }
        return new TxResult(tx.Hash, false, "timeout");
    }

    /// Build the UNSIGNED approve_material(id) transaction for the owner to sign in their
    /// browser wallet. Returns the JSON shape CSPR.click `send()` / Casper Wallet
    /// `signTransactionV1` expect: {"transaction":{"Version1":{…}}}. The server constructs
    /// the tx but never signs it — the owner's key stays in the wallet. approve_material is
    /// owner-gated on-chain, so only the owner's signature can make it succeed.
    public string BuildUnsignedApproveMaterialJson(uint id)
    {
        if (_ownerKey is null) throw new InvalidOperationException("no owner public key configured (Casper:OwnerPublicKeyHex)");

        var tx = new Transaction.ContractCallBuilder()
            .ByPackageHash(_pkg, null, null)
            .EntryPoint("approve_material")
            .RuntimeArgs(new List<NamedArg> { new NamedArg("id", CLValue.U32(id)) })
            .From(_ownerKey)
            .ChainName(_cfg["Casper:ChainName"]!)
            .Payment(30_000_000_000UL, 1)
            .Build(); // NOT signed — the wallet adds the owner's signature

        // The C# SDK serializes a Transaction as {"Deploy":null,"Version1":{…}}. Lift the
        // Version1 node and wrap it the way the wallet SDKs consume it.
        var root = JsonNode.Parse(JsonSerializer.Serialize(tx))!.AsObject();
        var v1 = root["Version1"]!.DeepClone();
        var wrapped = new JsonObject { ["transaction"] = new JsonObject { ["Version1"] = v1 } };
        return wrapped.ToJsonString();
    }

    /// Confirm a wallet-submitted co-sign: verify on-chain that the tx executed
    /// successfully AND that it genuinely called approve_material(id) on THIS vault — so a
    /// random successful tx hash can't forge a "co-signed" audit entry. Because
    /// approve_material is owner-gated in the contract, a SUCCESSFUL one proves the owner
    /// signed it; the server never needs to (and can't) hold the owner key.
    public async Task<TxResult> ConfirmCoSign(uint id, string txHash)
    {
        if (string.IsNullOrWhiteSpace(txHash)) return new TxResult("", false, "missing txHash");
        var client = Rpc();
        for (var i = 0; i < 24; i++)
        {
            try
            {
                var parsed = (await client.GetTransaction(txHash, CancellationToken.None)).Parse();
                var er = parsed.ExecutionInfo?.ExecutionResult;
                if (er is null) { await Task.Delay(5000); continue; }
                if (!er.IsSuccess) return new TxResult(txHash, false, er.ErrorMessage);
                var why = VerifyApproveMaterial(parsed.Transaction, id);
                return why is null ? new TxResult(txHash, true, null) : new TxResult(txHash, false, why);
            }
            catch { /* not yet visible to this node — keep polling */ }
            await Task.Delay(5000);
        }
        return new TxResult(txHash, false, "timeout waiting for on-chain confirmation");
    }

    /// Verify a fetched tx really is approve_material(id) on this package (and, if known,
    /// initiated by the owner). Returns null if valid, else the rejection reason. The
    /// entry-point + target checks alone defeat a random-hash forge; the id + initiator
    /// checks are defence-in-depth and only reject on a definite mismatch (never on a
    /// parse quirk, so a genuine co-sign is never wrongly rejected).
    private string? VerifyApproveMaterial(Transaction tx, uint id)
    {
        JsonObject? fields, v1;
        try
        {
            v1 = JsonNode.Parse(JsonSerializer.Serialize(tx))?["Version1"]?.AsObject();
            fields = v1?["payload"]?["fields"]?.AsObject();
        }
        catch (Exception ex) { return $"could not parse tx: {ex.Message}"; }
        if (fields is null) return "tx has no Version1 payload (not a TransactionV1)";

        if (!string.Equals(fields["entry_point"]?["Custom"]?.GetValue<string>(), "approve_material", StringComparison.OrdinalIgnoreCase))
            return "tx is not an approve_material call";
        var addr = fields["target"]?["Stored"]?["id"]?["ByPackageHash"]?["addr"]?.GetValue<string>()?.Replace("hash-", "");
        if (!string.Equals(addr, _pkg, StringComparison.OrdinalIgnoreCase))
            return "tx targets a different contract";

        // id arg — reject only on a definite mismatch.
        try
        {
            foreach (var pair in fields["args"]?["Named"]?.AsArray() ?? new JsonArray())
            {
                var arr = pair?.AsArray();
                if (arr is { Count: 2 } && arr[0]?.GetValue<string>() == "id" && arr[1]?["parsed"] is JsonNode p
                    && (uint)p.GetValue<long>() != id)
                    return $"tx approves a different proposal";
            }
        }
        catch { /* arg shape varied — entry-point + target already proved authenticity */ }

        // initiator — reject only if we know the owner and it's a definite mismatch.
        if (_ownerKey is not null)
        {
            var initiator = v1?["payload"]?["initiator_addr"]?["PublicKey"]?.GetValue<string>();
            if (initiator is not null && !string.Equals(initiator, _ownerKey.ToAccountHex(), StringComparison.OrdinalIgnoreCase))
                return "tx was not initiated by the owner";
        }
        return null;
    }

    private async Task<TxResult> Call(string entryPoint, List<NamedArg> args, ulong paymentMotes = 5_000_000_000UL)
    {
        if (_agentKp is null) return new TxResult("", false, "read-only: no agent key configured (observer mode)");
        var tx = new Transaction.ContractCallBuilder()
            .ByPackageHash(_pkg, null, null)
            .EntryPoint(entryPoint)
            .RuntimeArgs(args)
            .From(_agentKp.PublicKey)
            .ChainName(_cfg["Casper:ChainName"]!)
            .Payment(paymentMotes, 1)
            .Build();
        tx.Sign(_agentKp);

        var client = Rpc();
        await client.PutTransaction(tx);
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(5000);
            var er = (await client.GetTransaction(tx.Hash, CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null) return new TxResult(tx.Hash, er.IsSuccess, er.IsSuccess ? null : er.ErrorMessage);
        }
        return new TxResult(tx.Hash, false, "timeout");
    }

    private NetCasperClient Rpc()
    {
        var http = new HttpClient();
        var key = _cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Add("Authorization", key);
        return new NetCasperClient(_cfg["Casper:NodeRpcUrl"], http);
    }
}

public readonly record struct TxResult(string Hash, bool Success, string? Error)
{
    public string Url => $"https://testnet.cspr.live/transaction/{Hash}";
}
