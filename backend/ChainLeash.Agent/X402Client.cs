using System.Net;
using System.Net.Http.Json;
using Casper.Network.SDK;
using Casper.Network.SDK.Types;

namespace ChainLeash.Agent;

/// x402 consumer: fetch the premium signal, and on HTTP 402 pay the fee as a real
/// Casper transfer, then retry with the settlement hash as proof. This is the
/// agent's "pay-to-think" — it only pays when it decides the data is worth it.
/// The mock provider accepts the proof as-is; a real Casper x402 facilitator
/// (CSPR.cloud) would verify the settlement — swappable without touching this code.
public sealed class X402Client
{
    private readonly IConfiguration _cfg;
    private readonly KeyPair _agentKp;
    private readonly PublicKey _providerPk;
    private readonly HttpClient _http = new();

    public X402Client(IConfiguration cfg)
    {
        _cfg = cfg;
        _agentKp = KeyPair.FromPem(cfg["Casper:AgentSecretKeyPath"]!);
        var providerPath = cfg["X402:ProviderPubKeyPath"] ?? "../../spike/ChainLeash.Spike/secrets/human/public_key_hex";
        _providerPk = PublicKey.FromHexString(File.ReadAllText(providerPath).Trim());
    }

    public sealed record Signal(double Rate, string Risk, string SettlementHash, ulong PaidMotes);

    public async Task<Signal> BuySignal()
    {
        var url = _cfg["X402:SignalUrl"] ?? "http://localhost:5080/rate";
        var first = await _http.GetAsync(url);
        if (first.StatusCode != HttpStatusCode.PaymentRequired)
        {
            var open = await first.Content.ReadFromJsonAsync<RateDto>();
            return new Signal(open!.rate, open.risk, "", 0);
        }

        var ch = await first.Content.ReadFromJsonAsync<ChallengeDto>();
        var amount = ulong.Parse(ch!.maxAmountRequired);

        // Pay the fee over Casper (native transfer to the provider's purse).
        var pay = new Transaction.NativeTransferBuilder()
            .Target(_providerPk)
            .Amount(amount)
            .From(_agentKp.PublicKey)
            .ChainName(_cfg["Casper:ChainName"]!)
            .Payment(100_000_000UL, 1)
            .Build();
        pay.Sign(_agentKp);

        var http = new HttpClient();
        var key = _cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Add("Authorization", key);
        await new NetCasperClient(_cfg["Casper:NodeRpcUrl"], http).PutTransaction(pay);

        // Retry with the on-chain payment proof.
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Payment", pay.Hash);
        var second = await _http.SendAsync(req);
        var dto = await second.Content.ReadFromJsonAsync<RateDto>();
        return new Signal(dto!.rate, dto.risk, pay.Hash, amount);
    }

    private sealed record ChallengeDto(string scheme, string payTo, string maxAmountRequired);
    private sealed record RateDto(double rate, string risk, string? paidWith);
}
