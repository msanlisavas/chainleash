// CHAINLEASH x402 signal provider (mock facilitator).
// GET /rate returns HTTP 402 Payment Required until the caller presents an
// X-Payment header (a Casper transfer deploy hash). This is the consumer-facing
// half of x402 "pay-per-request"; a production facilitator (CSPR.cloud) would
// cryptographically verify the settlement before serving. Kept consumer-side so
// it's swappable for the real Casper x402 facilitator without touching the agent.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// payTo = the provider's account hash (the agent transfers the fee here).
var payTo = app.Configuration["X402:PayToAccountHash"]
            ?? "5bc1cf012c678676ff14c3cd3d2d72ac19d17819d448de4795f7bf1618bfd232";
var priceMotes = app.Configuration.GetValue<long>("X402:PriceMotes", 400_000_000L); // 0.4 CSPR
var rng = new Random();

app.MapGet("/", () => "CHAINLEASH x402 signal provider. GET /rate (HTTP 402 until paid).");

app.MapGet("/rate", (HttpContext ctx) =>
{
    var proof = ctx.Request.Headers["X-Payment"].ToString();
    if (string.IsNullOrWhiteSpace(proof))
    {
        // 402 + the payment requirements (x402-style challenge).
        return Results.Json(new
        {
            x402Version = 1,
            scheme = "casper-transfer",
            network = "casper-test",
            payTo,
            maxAmountRequired = priceMotes.ToString(),
            resource = "/rate",
            description = "Premium rate/risk signal — pay the required CSPR to payTo per read."
        }, statusCode: StatusCodes.Status402PaymentRequired);
    }

    // Mock-accept the on-chain payment proof (a real facilitator would verify the
    // transfer settled `payTo` for >= maxAmountRequired). Serve the premium signal.
    var phase = DateTime.UtcNow.Ticks / 1.0e11;
    var rate = Math.Round(4.6 + Math.Sin(phase) * 0.7 + (rng.NextDouble() - 0.5) * 0.3, 2);
    return Results.Json(new
    {
        rate,
        risk = rate > 5.0 ? "elevated" : "low",
        paidWith = proof
    });
});

app.Run();
