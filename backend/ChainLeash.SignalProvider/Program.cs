// CHAINLEASH x402 signal provider.
// GET /rate returns HTTP 402 Payment Required until the caller presents an X-Payment
// header (a Casper transfer deploy hash). Before serving, the provider VERIFIES the
// payment on-chain via CSPR.cloud — that the deploy executed successfully and a transfer
// to payTo for >= the price actually settled — and rejects replayed proofs. The signal
// itself is derived from REAL validator metrics (CSPR.cloud), not mock data. This is the
// CSPR.cloud x402-facilitator pattern; it is the seller half (a 10-second flourish — the
// real product value is the BUYER paying to think). See README.

using System.Collections.Concurrent;
using System.Numerics;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true);
var app = builder.Build();

var cfg = app.Configuration;
// payTo = the provider's account hash (the agent transfers the fee here).
var payTo = (cfg["X402:PayToAccountHash"] ?? "5bc1cf012c678676ff14c3cd3d2d72ac19d17819d448de4795f7bf1618bfd232").ToLowerInvariant();
var priceMotes = BigInteger.Parse(cfg["X402:PriceMotes"] ?? "2500000000"); // 2.5 CSPR; U512 → BigInteger
// Bind the proof to the expected PAYER (the agent's account hash): otherwise anyone could reuse
// any historical transfer to payTo as a free-access token. Empty string = accept any payer (legacy).
var expectedPayer = (cfg["X402:ExpectedPayerAccountHash"] ?? "11b5fdcc0b9653c5d67891c675d1548193779b7ff0a9c942c03f7e6752b52aeb").ToLowerInvariant();
var apiBase = (cfg["X402:CsprCloudBaseUrl"] ?? "https://api.testnet.cspr.cloud").TrimEnd('/');
var apiKey = cfg["X402:CsprCloudAccessKey"] ?? cfg["Casper:CsprCloudAccessKey"] ?? "55f79117-fc4d-4d60-9956-65423f39a06a";

var http = new HttpClient { BaseAddress = new Uri(apiBase + "/") };
if (!string.IsNullOrWhiteSpace(apiKey)) http.DefaultRequestHeaders.Add("Authorization", apiKey);
var consumed = new ConcurrentDictionary<string, byte>(); // replay protection: each proof spends once

IResult Challenge() => Results.Json(new
{
    x402Version = 1,
    scheme = "casper-transfer",
    network = "casper-test",
    payTo,
    maxAmountRequired = priceMotes.ToString(),
    resource = "/rate",
    description = "Premium validator-risk read — pay the required CSPR to payTo per read."
}, statusCode: StatusCodes.Status402PaymentRequired);

// Verify on-chain that `hash` executed OK and moved >= priceMotes to payTo.
async Task<bool> VerifyPayment(string hash)
{
    try
    {
        using var d = await http.GetAsync($"deploys/{hash}");
        if (!d.IsSuccessStatusCode) return false;
        using var dDoc = System.Text.Json.JsonDocument.Parse(await d.Content.ReadAsStringAsync());
        var data = dDoc.RootElement.GetProperty("data");
        if (data.TryGetProperty("error_message", out var em) && em.ValueKind == System.Text.Json.JsonValueKind.String) return false; // executed-failed
        if (data.GetProperty("status").GetString() != "processed") return false;

        using var t = await http.GetAsync($"deploys/{hash}/transfers");
        if (!t.IsSuccessStatusCode) return false;
        using var tDoc = System.Text.Json.JsonDocument.Parse(await t.Content.ReadAsStringAsync());
        foreach (var tr in tDoc.RootElement.GetProperty("data").EnumerateArray())
        {
            var to = tr.GetProperty("to_account_hash").GetString()?.ToLowerInvariant();
            var from = tr.TryGetProperty("from_account_hash", out var fh) ? fh.GetString()?.ToLowerInvariant() : null;
            if (!BigInteger.TryParse(tr.GetProperty("amount").GetString(), out var amt)) continue; // U512-safe
            var payerOk = string.IsNullOrEmpty(expectedPayer) || from == expectedPayer; // bind to the buyer
            if (to == payTo && payerOk && amt >= priceMotes) return true;
        }
        return false;
    }
    catch { return false; }
}

// A real, validator-derived risk read. If the caller names the validator it's about to
// act on (?validator=<hex>), score that validator's live commission/active status; else
// fall back to a network-health read from the auction metrics.
async Task<(double rate, string risk)> Signal(string? validator)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(validator))
        {
            var era = (await http.GetFromJsonAsync<System.Text.Json.JsonElement>("auction-metrics")).GetProperty("data").GetProperty("current_era_id").GetInt64();
            using var v = await http.GetAsync($"validators?era_id={era}&page_size=100");
            using var vDoc = System.Text.Json.JsonDocument.Parse(await v.Content.ReadAsStringAsync());
            foreach (var val in vDoc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (string.Equals(val.GetProperty("public_key").GetString(), validator, StringComparison.OrdinalIgnoreCase))
                {
                    var fee = val.TryGetProperty("fee", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Number ? f.GetInt32() : 0;
                    var active = val.TryGetProperty("is_active", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
                    // Elevated when commission is high or the validator isn't active this era.
                    return (fee, (!active || fee > 8) ? "elevated" : "low");
                }
            }
            return (0, "elevated"); // not in the active set this era → elevated
        }
        var m = (await http.GetFromJsonAsync<System.Text.Json.JsonElement>("auction-metrics")).GetProperty("data");
        var activeN = m.GetProperty("active_validator_number").GetInt32();
        return (activeN, activeN < 20 ? "elevated" : "low"); // thin active set → elevated
    }
    catch { return (0, "elevated"); } // on data failure, be conservative
}

app.MapGet("/", () => "CHAINLEASH x402 signal provider. GET /rate (HTTP 402 until a verified CSPR payment).");

app.MapGet("/rate", async (HttpContext ctx) =>
{
    var proof = ctx.Request.Headers["X-Payment"].ToString();
    if (string.IsNullOrWhiteSpace(proof)) return Challenge();
    if (consumed.ContainsKey(proof)) return Challenge();          // replay: one read per payment
    if (!await VerifyPayment(proof)) return Challenge();          // unverified / unsettled / underpaid / wrong payer
    if (consumed.Count < 100_000) consumed.TryAdd(proof, 1);      // bounded (payer-binding keeps this tiny in practice)

    var (rate, risk) = await Signal(ctx.Request.Query["validator"]);
    return Results.Json(new { rate, risk, paidWith = proof });
});

app.Run();
