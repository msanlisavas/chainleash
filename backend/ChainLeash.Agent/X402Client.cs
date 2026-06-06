using System.Net;
using System.Net.Http.Json;
using Casper.Network.SDK;
using Casper.Network.SDK.Types;

namespace ChainLeash.Agent;

/// x402 consumer: fetch the premium signal, and on HTTP 402 pay the fee as a real Casper
/// transfer, AWAIT its on-chain settlement, then retry with the settlement hash as proof.
/// This is the agent's "pay-to-think" — it only pays when it decides the data is worth it,
/// and it never claims to have paid until the transfer actually executed. The provider
/// verifies the settlement on-chain before serving (see ChainLeash.SignalProvider).
public sealed class X402Client
{
    private readonly IConfiguration _cfg;
    private readonly KeyPair? _agentKp;
    private readonly PublicKey? _providerPk;
    private readonly HttpClient _http = new();

    public X402Client(IConfiguration cfg)
    {
        _cfg = cfg;
        // Both loads are optional: with no agent key / provider pubkey the agent runs
        // OBSERVER-ONLY (it can't pay-to-think), so the host still boots cleanly.
        var agentPath = cfg["Casper:AgentSecretKeyPath"];
        if (!string.IsNullOrWhiteSpace(agentPath) && File.Exists(agentPath))
            _agentKp = KeyPair.FromPem(agentPath);
        var providerPath = cfg["X402:ProviderPubKeyPath"] ?? "../../spike/ChainLeash.Spike/secrets/human/public_key_hex";
        if (File.Exists(providerPath))
            _providerPk = PublicKey.FromHexString(File.ReadAllText(providerPath).Trim());
    }

    /// True when no agent key / provider pubkey is configured — pay-to-think is unavailable.
    public bool Disabled => _agentKp is null || _providerPk is null;

    public sealed record Signal(double Rate, string Risk, string SettlementHash, ulong PaidMotes);

    /// Buy the premium risk read for a specific candidate validator (so the signal is a
    /// real, relevant read). Pays over x402 and only returns once the payment has settled.
    public async Task<Signal> BuySignal(string? validatorHex = null)
    {
        if (_agentKp is null || _providerPk is null)
            throw new InvalidOperationException("x402 pay-to-think disabled — no agent key / provider pubkey (observer mode)");
        var agentKp = _agentKp!; var providerPk = _providerPk!;
        var baseUrl = _cfg["X402:SignalUrl"] ?? "http://localhost:5080/rate";
        var url = string.IsNullOrEmpty(validatorHex) ? baseUrl : $"{baseUrl}?validator={validatorHex}";

        var first = await _http.GetAsync(url);
        if (first.StatusCode != HttpStatusCode.PaymentRequired)
        {
            var open = await first.Content.ReadFromJsonAsync<RateDto>();
            return new Signal(open!.rate, open.risk, "", 0);
        }

        var ch = await first.Content.ReadFromJsonAsync<ChallengeDto>();
        var amount = ulong.Parse(ch!.maxAmountRequired);

        // Pay the fee over Casper (native transfer to the provider's account).
        var pay = new Transaction.NativeTransferBuilder()
            .Target(providerPk)
            .Amount(amount)
            .From(agentKp.PublicKey)
            .ChainName(_cfg["Casper:ChainName"]!)
            .Payment(100_000_000UL, 1)
            .Build();
        pay.Sign(agentKp);

        var client = Rpc();
        await client.PutTransaction(pay);

        // AWAIT on-chain settlement — don't claim "paid" until the transfer executed OK.
        var settled = false;
        for (var i = 0; i < 24 && !settled; i++)
        {
            await Task.Delay(5000);
            var er = (await client.GetTransaction(pay.Hash, CancellationToken.None)).Parse().ExecutionInfo?.ExecutionResult;
            if (er is not null)
            {
                if (!er.IsSuccess) throw new Exception($"x402 payment failed on-chain: {er.ErrorMessage}");
                settled = true;
            }
        }
        if (!settled) throw new Exception("x402 payment did not settle in time");

        // Retry with the verified on-chain payment proof; the provider checks it before serving.
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Payment", pay.Hash);
        var second = await _http.SendAsync(req);
        if (second.StatusCode == HttpStatusCode.PaymentRequired)
            throw new Exception("provider rejected the payment proof");
        var dto = await second.Content.ReadFromJsonAsync<RateDto>();
        return new Signal(dto!.rate, dto.risk, pay.Hash, amount);
    }

    private NetCasperClient Rpc()
    {
        var http = new HttpClient();
        var key = _cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Add("Authorization", key);
        return new NetCasperClient(_cfg["Casper:NodeRpcUrl"], http);
    }

    private sealed record ChallengeDto(string scheme, string payTo, string maxAmountRequired);
    private sealed record RateDto(double rate, string risk, string? paidWith);
}
