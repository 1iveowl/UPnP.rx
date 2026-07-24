using UPnP.Rx.Model;
using Xunit;

namespace UPnP.Rx.Tests;

public class ParseResultTests
{
    [Fact]
    public void Success_CarriesValue()
    {
        var result = ParseResult<string>.Success("value");

        Assert.True(result.IsSuccess);
        Assert.Equal("value", result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_CarriesError()
    {
        var result = ParseResult<string>.Failure("broken");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal("broken", result.Error);
    }

    [Fact]
    public void Match_AppliesTheMatchingBranch()
    {
        Assert.Equal(5, ParseResult<string>.Success("value").Match(v => v.Length, _ => -1));
        Assert.Equal(-1, ParseResult<string>.Failure("broken").Match(v => v.Length, _ => -1));
    }
}
