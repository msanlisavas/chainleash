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
    private readonly KeyPair _agentKp;
    private readonly string _pkg;

    public CasperVault(IConfiguration cfg)
    {
        _cfg = cfg;
        _agentKp = KeyPair.FromPem(cfg["Casper:AgentSecretKeyPath"]!);
        _pkg = cfg["Casper:GovernedVaultPackageHash"]!.Replace("hash-", "");
    }

    public PublicKey AgentKey => _agentKp.PublicKey;

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

    private async Task<TxResult> Call(string entryPoint, List<NamedArg> args, ulong paymentMotes = 5_000_000_000UL)
    {
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
