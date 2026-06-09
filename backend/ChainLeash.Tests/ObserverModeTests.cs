using Casper.Network.SDK.Types;
using ChainLeash.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ChainLeash.Tests;

/// With no agent key present, the CasperVault layer must construct cleanly and refuse to
/// sign. (The end-to-end observer behavior — keyless `docker compose up` boots, serves the
/// dashboard, and never acts — is verified live against testnet, not here.)
public class ObserverModeTests
{
    static IConfiguration Cfg() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Casper:AgentSecretKeyPath"] = "D:/definitely/nonexistent/agent.pem",
        ["Casper:GovernedVaultPackageHash"] = "hash-" + new string('a', 64),
        ["Casper:ChainName"] = "casper-test",
        ["Casper:NodeRpcUrl"] = "http://localhost:0",
    }).Build();

    [Fact]
    public void No_agent_key_means_read_only()
    {
        var v = new CasperVault(Cfg());
        Assert.True(v.ReadOnly);
        Assert.Null(v.AgentKey);
    }

    [Fact]
    public async Task Read_only_vault_signs_nothing()
    {
        var v = new CasperVault(Cfg());
        var validator = PublicKey.FromHexString("0106618e1493f73ee0bc67ffbad4ba4e3863b995d61786d9b9a68ec7676f697981");
        var r = await v.Delegate(validator, 1_000_000_000UL); // returns before any signing/network
        Assert.False(r.Success);
        Assert.Contains("read-only", r.Error);
    }
}
