using ChainLeash.Agent;
using Xunit;

namespace ChainLeash.Tests;

public class ValidatorNamesTests
{
    [Fact]
    public void Parse_extracts_registered_names_lowercased_keys()
    {
        var json = """
        {"data":[
          {"public_key":"0106618E14","account_info":{"info":{"owner":{"name":"dsg"}}}},
          {"public_key":"017d96b9a6","account_info":{"info":{"owner":{"name":" MAKE "}}}},
          {"public_key":"01a1622449","account_info":null},
          {"public_key":"0147ce053a"},
          {"public_key":"017c5037fc","account_info":{"info":{"owner":{"name":""}}}}
        ]}
        """;
        var names = ValidatorNames.Parse(json);
        Assert.Equal("dsg", names["0106618e14"]);     // key lower-cased
        Assert.Equal("MAKE", names["017d96b9a6"]);    // name trimmed
        Assert.False(names.ContainsKey("01a1622449")); // account_info null → no name
        Assert.False(names.ContainsKey("0147ce053a")); // no account_info → no name
        Assert.False(names.ContainsKey("017c5037fc")); // empty name → skipped
    }

    [Fact]
    public void Parse_tolerates_missing_or_empty_data()
    {
        Assert.Empty(ValidatorNames.Parse("""{"data":[]}"""));
        Assert.Empty(ValidatorNames.Parse("""{}"""));
    }
}
