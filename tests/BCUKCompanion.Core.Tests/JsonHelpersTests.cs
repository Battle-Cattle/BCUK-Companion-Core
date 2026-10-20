using Xunit;

namespace BCUKCompanion.Core.Tests;

public sealed class JsonHelpersTests
{
    [Theory]
    [InlineData("""{"error":"bad code"}""", "bad code")]
    [InlineData("""{"other":1,"error":"x"}""", "x")]
    [InlineData("""{"error":""}""", "")]
    [InlineData("""{"error":5}""", null)] // not a string
    [InlineData("""{"error":null}""", null)]
    [InlineData("""{"nested":{"error":"x"}}""", null)] // top level only
    [InlineData("""["error"]""", null)] // not an object
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void TryGetString(string json, string? expected)
    {
        Assert.Equal(expected, JsonHelpers.TryGetString(json, "error"));
    }
}
