using ChainLeash.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ChainLeash.Tests;

/// The node-RPC resilience layer that keeps a transient free-node hiccup (stale pooled socket → the
/// "request was canceled … 30 seconds" HOLD) from stalling a tick: classify what's worth retrying,
/// and derive the CSPR.cloud node-RPC proxy used as the reliable fallback. (The live HttpClient
/// failover itself is exercised on-chain, per the project's "network code is verified live" rule.)
public class ChainReaderResilienceTests
{
    static IConfiguration Cfg(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            pairs.ToDictionary(p => p.Key, p => p.Value)).Build();

    // ── IsTransient: only transport faults + 429/5xx retry; a real answer never does ──

    [Fact]
    public void Transport_faults_are_transient()
    {
        Assert.True(ChainReader.IsTransient(new TaskCanceledException()));      // HttpClient.Timeout
        Assert.True(ChainReader.IsTransient(new OperationCanceledException()));
        Assert.True(ChainReader.IsTransient(new HttpRequestException("boom"))); // connect/DNS/reset
        Assert.True(ChainReader.IsTransient(new IOException("transport")));     // socket read canceled (125)
    }

    [Fact]
    public void Http_429_and_5xx_are_transient()
    {
        Assert.True(ChainReader.IsTransient(new ChainRpcException("m", "HTTP 429", transient: true)));
        Assert.True(ChainReader.IsTransient(new ChainRpcException("m", "HTTP 503", transient: true)));
    }

    [Fact]
    public void Value_not_found_and_rpc_errors_are_NOT_transient()
    {
        // An unset dictionary key is a real answer (field never written) — retrying is wrong.
        Assert.False(ChainReader.IsTransient(new ChainRpcException("m", "not found", notFound: true)));
        // A parsed RPC-level error from a healthy node — a retry won't change it.
        Assert.False(ChainReader.IsTransient(new ChainRpcException("m", "Some error (-32000)")));
        // A totally unrelated exception must never trip the fallback.
        Assert.False(ChainReader.IsTransient(new InvalidOperationException()));
    }

    // ── FallbackNodeUrl: derive node.<net>.cspr.cloud, honor explicit config, never mirror primary ──

    const string PublicNode = "https://node.testnet.casper.network/rpc";

    [Fact]
    public void Derives_testnet_proxy_from_the_default_rest_base()
    {
        var url = ChainReader.FallbackNodeUrl(Cfg(), PublicNode);
        Assert.Equal("https://node.testnet.cspr.cloud/rpc", url);
    }

    [Fact]
    public void Derives_mainnet_proxy_from_the_mainnet_rest_base()
    {
        var url = ChainReader.FallbackNodeUrl(
            Cfg(("Casper:CsprCloudBaseUrl", "https://api.mainnet.cspr.cloud")), PublicNode);
        Assert.Equal("https://node.mainnet.cspr.cloud/rpc", url);
    }

    [Fact]
    public void Explicit_node_rpc_url_wins_over_derivation()
    {
        var url = ChainReader.FallbackNodeUrl(
            Cfg(("Casper:CsprCloudNodeRpcUrl", "https://my.node/rpc"),
                ("Casper:CsprCloudBaseUrl", "https://api.testnet.cspr.cloud")), PublicNode);
        Assert.Equal("https://my.node/rpc", url);
    }

    [Fact]
    public void Null_when_the_fallback_would_equal_the_primary()
    {
        // Already pointing the primary at the CSPR.cloud proxy → no self-retry.
        var url = ChainReader.FallbackNodeUrl(
            Cfg(("Casper:CsprCloudNodeRpcUrl", "https://node.testnet.cspr.cloud/rpc")),
            "https://node.testnet.cspr.cloud/rpc");
        Assert.Null(url);
    }
}
