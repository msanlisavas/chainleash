using Casper.Network.SDK;
using Casper.Network.SDK.Types;

namespace ChainLeash.Agent;

/// On-chain interface to the live GovernedVault (Casper 2.0 testnet).
/// Calls entry points by package hash, signed by the agent key. Reuses the
/// patterns proven in the spike harness (see finding 05).
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

    private NetCasperClient Rpc()
    {
        var http = new HttpClient();
        var key = _cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Add("Authorization", key);
        return new NetCasperClient(_cfg["Casper:NodeRpcUrl"], http);
    }

    /// Agent proposes an over-cap (material) move — emits MaterialProposed on-chain,
    /// awaiting the human's approval. No treasury funding required.
    public Task<TxResult> ProposeMaterial(PublicKey counterparty, ulong amountMotes) =>
        Call("propose_material", new List<NamedArg>
        {
            new NamedArg("counterparty", CLValue.KeyFromPublicKey(counterparty)),
            new NamedArg("amount", CLValue.U512(amountMotes)),
        });

    /// Overseer tightens (only lowers) the cap — emits CapTightened.
    public Task<TxResult> TightenCap(ulong newCapMotes) =>
        Call("tighten_cap", new List<NamedArg> { new NamedArg("new_cap", CLValue.U512(newCapMotes)) });

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
}

public readonly record struct TxResult(string Hash, bool Success, string? Error)
{
    public string Url => $"https://testnet.cspr.live/transaction/{Hash}";
}
