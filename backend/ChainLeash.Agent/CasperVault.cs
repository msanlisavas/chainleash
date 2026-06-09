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
    public Task<TxResult> Delegate(PublicKey validator, ulong amountMotes, CancellationToken ct = default) =>
        Call("delegate", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        }, paymentMotes: 30_000_000_000UL, ct);

    /// Routine autonomous undelegation (≤ cap). Funds unbond back to the VAULT,
    /// never to the agent — the agent can rebalance but can't drain.
    public Task<TxResult> Undelegate(PublicKey validator, ulong amountMotes, CancellationToken ct = default) =>
        Call("undelegate", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        }, paymentMotes: 30_000_000_000UL, ct);

    /// Routine autonomous redelegation — move stake from one validator to another in a
    /// single native tx (≤ cap, destination allowlisted). The cleanest "switch to a
    /// better validator" move: no manual unstake/restake, destination committed on-chain.
    public Task<TxResult> Redelegate(PublicKey from, PublicKey to, ulong amountMotes, CancellationToken ct = default) =>
        Call("redelegate", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(from)),
            new NamedArg("new_validator", CLValue.PublicKey(to)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        }, paymentMotes: 30_000_000_000UL, ct);

    /// Agent proposes an over-cap (material) (un)delegation — emits MaterialProposed
    /// on-chain and waits for the human owner to co-sign approve_material.
    public Task<TxResult> ProposeMaterial(PublicKey validator, ulong amountMotes, bool undelegate, CancellationToken ct = default) =>
        Call("propose_material", new List<NamedArg>
        {
            new NamedArg("validator", CLValue.PublicKey(validator)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
            new NamedArg("undelegate", CLValue.Bool(undelegate)),
        }, ct: ct);

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
        await Rpc().PutTransaction(tx);
        return await AwaitExecution(tx.Hash);
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

        await Rpc().PutTransaction(tx);
        return await AwaitExecution(tx.Hash);
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
    public async Task<TxResult> ConfirmCoSign(uint id, string txHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(txHash)) return new TxResult("", false, "missing txHash");
        var client = Rpc();
        try
        {
            for (var i = 0; i < 24; i++)
            {
                try
                {
                    var parsed = (await client.GetTransaction(txHash, ct)).Parse();
                    var er = parsed.ExecutionInfo?.ExecutionResult;
                    if (er is null) { await Task.Delay(5000, ct); continue; }
                    if (!er.IsSuccess) return new TxResult(txHash, false, OdraErrors.Humanize(er.ErrorMessage));
                    var why = VerifyApproveMaterial(parsed.Transaction, id);
                    return why is null ? new TxResult(txHash, true, null) : new TxResult(txHash, false, why);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* not yet visible to this node — keep polling */ }
                await Task.Delay(5000, ct);
            }
        }
        // The caller went away (browser closed) — stop burning upstream RPCs on its behalf.
        catch (OperationCanceledException) { return new TxResult(txHash, false, "cancelled: client disconnected (timeout)"); }
        return new TxResult(txHash, false, "timeout waiting for on-chain confirmation");
    }

    /// Verify a fetched tx really is approve_material(id) on this package, owner-initiated.
    /// Delegates to the pure, unit-tested CoSignVerifier (fail-closed on every check).
    private string? VerifyApproveMaterial(Transaction tx, uint id)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(JsonSerializer.Serialize(tx))?.AsObject(); }
        catch (Exception ex) { return $"could not parse tx: {ex.Message}"; }
        return CoSignVerifier.Verify(root, _pkg, id, _ownerKey?.ToAccountHex(), _ownerKey?.GetAccountHash());
    }

    private async Task<TxResult> Call(string entryPoint, List<NamedArg> args, ulong paymentMotes = 5_000_000_000UL, CancellationToken ct = default)
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

        await Rpc().PutTransaction(tx);
        return await AwaitExecution(tx.Hash, ct);
    }

    /// Poll until the submitted tx executes. A transient GetTransaction failure (node lag,
    /// a 429 on the shared CSPR.cloud key) must NOT abandon a tx that was already put on
    /// chain — the caller would wrongly retry and duplicate the action — so errors inside
    /// the loop are tolerated until the 2-minute budget runs out. Honors cancellation so
    /// host shutdown isn't held hostage by a poll.
    private async Task<TxResult> AwaitExecution(string hash, CancellationToken ct = default)
    {
        try
        {
            for (var i = 0; i < 24; i++)
            {
                await Task.Delay(5000, ct);
                try
                {
                    var er = (await Rpc().GetTransaction(hash, ct)).Parse().ExecutionInfo?.ExecutionResult;
                    if (er is not null) return new TxResult(hash, er.IsSuccess, er.IsSuccess ? null : OdraErrors.Humanize(er.ErrorMessage));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* transient — keep polling */ }
            }
        }
        catch (OperationCanceledException) { return new TxResult(hash, false, "cancelled while awaiting confirmation"); }
        return new TxResult(hash, false, "timeout awaiting on-chain confirmation");
    }

    private NetCasperClient? _rpc;
    // One shared client for the life of the service — a fresh HttpClient per call leaks sockets
    // in a 24/7 daemon (NetCasperClient isn't IDisposable, so the inner client never closes).
    private NetCasperClient Rpc()
    {
        if (_rpc is not null) return _rpc;
        var http = new HttpClient();
        var key = _cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Add("Authorization", key);
        return _rpc = new NetCasperClient(_cfg["Casper:NodeRpcUrl"], http);
    }
}

public readonly record struct TxResult(string Hash, bool Success, string? Error)
{
    public string Url => $"{AuditEvent.ExplorerBase}/transaction/{Hash}";
}
