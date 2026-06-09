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
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public X402Client(IConfiguration cfg)
    {
        _cfg = cfg;
        // Both loads are optional: with no agent key / provider pubkey the agent runs
        // OBSERVER-ONLY (it can't pay-to-think), so the host still boots cleanly.
        var agentPath = cfg["Casper:AgentSecretKeyPath"];
        if (!string.IsNullOrWhiteSpace(agentPath) && File.Exists(agentPath))
            _agentKp = KeyPair.FromPem(agentPath);
        // The provider pubkey is PUBLIC material — accept it directly from config/env
        // (X402:ProviderPubKeyHex, the container-friendly path) before falling back to
        // the repo-relative file keygen writes.
        var providerHex = cfg["X402:ProviderPubKeyHex"];
        var providerPath = cfg["X402:ProviderPubKeyPath"] ?? "../../spike/ChainLeash.Spike/secrets/human/public_key_hex";
        if (!string.IsNullOrWhiteSpace(providerHex))
            _providerPk = PublicKey.FromHexString(providerHex.Trim());
        else if (File.Exists(providerPath))
            _providerPk = PublicKey.FromHexString(File.ReadAllText(providerPath).Trim());
    }

    /// True when no agent key / provider pubkey is configured — pay-to-think is unavailable.
    public bool Disabled => _agentKp is null || _providerPk is null;

    public sealed record Signal(double Rate, string Risk, string SettlementHash, ulong PaidMotes);

    /// Buy the premium risk read for a specific candidate validator (so the signal is a
    /// real, relevant read). Pays over x402 and only returns once the payment has settled.
    public async Task<Signal> BuySignal(string? validatorHex = null, CancellationToken ct = default)
    {
        if (_agentKp is null || _providerPk is null)
            throw new InvalidOperationException("x402 pay-to-think disabled — no agent key / provider pubkey (observer mode)");
        var agentKp = _agentKp!; var providerPk = _providerPk!;
        var baseUrl = _cfg["X402:SignalUrl"] ?? "http://localhost:5080/rate";
        var url = string.IsNullOrEmpty(validatorHex) ? baseUrl : $"{baseUrl}?validator={validatorHex}";

        var first = await _http.GetAsync(url, ct);
        if (first.StatusCode != HttpStatusCode.PaymentRequired)
        {
            var open = await first.Content.ReadFromJsonAsync<RateDto>(ct);
            return new Signal(open!.rate, open.risk, "", 0);
        }

        var ch = await first.Content.ReadFromJsonAsync<ChallengeDto>();
        var amount = ulong.Parse(ch!.maxAmountRequired);
        // Never overpay a (possibly MITM'd / hostile) provider, and refuse a redirected payTo.
        var maxFee = _cfg.GetValue<ulong>("X402:MaxFeeMotes", 10_000_000_000UL);
        if (amount > maxFee) throw new InvalidOperationException($"x402 fee {amount} motes exceeds cap {maxFee}");
        var providerAh = providerPk.GetAccountHash().Replace("account-hash-", "").ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ch.payTo) && ch.payTo.Replace("account-hash-", "").ToLowerInvariant() != providerAh)
            throw new InvalidOperationException("x402 challenge payTo does not match the configured provider");

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
        // Transient GetTransaction failures (node lag, 429) must not abandon a payment
        // that is already in flight, so errors inside the loop are tolerated.
        var settled = false;
        for (var i = 0; i < 24 && !settled; i++)
        {
            await Task.Delay(5000, ct);
            try
            {
                var er = (await client.GetTransaction(pay.Hash, ct)).Parse().ExecutionInfo?.ExecutionResult;
                if (er is not null)
                {
                    if (!er.IsSuccess) throw new Exception($"x402 payment failed on-chain: {er.ErrorMessage}");
                    settled = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ex.Message.StartsWith("x402"))
            { /* transient — keep polling */ }
        }
        if (!settled) throw new Exception($"x402 payment did not settle in time (tx {pay.Hash})");

        // Present the settled proof. Real CSPR is now spent, so neither a transient
        // provider error NOR a 402 may instantly strand the payment: the provider's
        // indexer (CSPR.cloud) can lag the node we confirmed against, and it releases
        // the consumed-claim on a failed verify precisely so the SAME hash can be
        // retried. Only after the retries are exhausted do we surface the proof hash —
        // via a typed exception so the caller can still account for the settled spend.
        for (var attempt = 1; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Payment", pay.Hash);
            var second = await _http.SendAsync(req, ct);
            if (second.IsSuccessStatusCode)
            {
                var dto = await second.Content.ReadFromJsonAsync<RateDto>(ct);
                return new Signal(dto!.rate, dto.risk, pay.Hash, amount);
            }
            if (attempt >= 4)
                throw new X402StrandedPaymentException(amount, pay.Hash,
                    second.StatusCode == HttpStatusCode.PaymentRequired
                        ? "provider kept rejecting the settled proof (indexer lag or misconfigured payTo/payer binding)"
                        : "provider unavailable after settlement");
            await Task.Delay(5000, ct); // indexer lag — the proof stays redeemable
        }
    }

    private NetCasperClient? _rpc;
    // Shared client — a fresh HttpClient per call leaks sockets over the agent's lifetime.
    private NetCasperClient Rpc()
    {
        if (_rpc is not null) return _rpc;
        var http = new HttpClient();
        var key = _cfg["Casper:CsprCloudAccessKey"];
        if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Add("Authorization", key);
        return _rpc = new NetCasperClient(_cfg["Casper:NodeRpcUrl"], http);
    }

    private sealed record ChallengeDto(string scheme, string payTo, string maxAmountRequired);
    private sealed record RateDto(double rate, string risk, string? paidWith);
}

/// The payment SETTLED on-chain but the provider never served the signal — the CSPR is
/// spent and the proof remains redeemable. Carries the amount so the caller can still
/// record the spend honestly instead of losing it from the books.
public sealed class X402StrandedPaymentException(ulong paidMotes, string settlementHash, string reason)
    : Exception($"x402 paid {paidMotes} motes (tx {settlementHash}) but no signal was served: {reason} — the proof remains redeemable")
{
    public ulong PaidMotes { get; } = paidMotes;
    public string SettlementHash { get; } = settlementHash;
}
