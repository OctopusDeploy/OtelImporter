using OtelImporter.Configuration;

namespace OtelImporter.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void ParsesPositionalInputFile()
    {
        var result = CommandLineParser.Parse(["traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.Equal("traces.jsonl", result.Options!.InputFiles[0]);
        Assert.Null(result.Options.Endpoint);
        Assert.Null(result.Options.Protocol);
    }

    [Theory]
    [InlineData("--endpoint")]
    [InlineData("-e")]
    public void ParsesEndpointFlag(string flag)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag, "http://localhost:4318"]);

        Assert.Null(result.Error);
        Assert.Equal("http://localhost:4318", result.Options!.Endpoint);
    }

    [Theory]
    [InlineData("--protocol", "grpc", OtlpProtocol.Grpc)]
    [InlineData("-p", "http", OtlpProtocol.Http)]
    [InlineData("-p", "GRPC", OtlpProtocol.Grpc)]
    public void ParsesProtocolFlag(string flag, string value, OtlpProtocol expected)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag, value]);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Options!.Protocol);
    }

    [Theory]
    [InlineData("--inspect")]
    [InlineData("-i")]
    public void ParsesInspectFlag(string flag)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.Inspect);
        Assert.Equal("traces.jsonl", result.Options.InputFiles[0]);
    }

    [Fact]
    public void InspectDefaultsToFalse()
    {
        var result = CommandLineParser.Parse(["traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.False(result.Options!.Inspect);
        Assert.False(result.Options.NoInspect);
    }

    [Fact]
    public void ParsesNoInspectFlag()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--no-inspect"]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.NoInspect);
        Assert.False(result.Options.Inspect);
    }

    [Fact]
    public void RejectsCombiningInspectAndNoInspect()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--inspect", "--no-inspect"]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    [Theory]
    [InlineData("--attribute")]
    [InlineData("-a")]
    public void ParsesRepeatedAttributes(string flag)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag, "octopus.prop=abc", flag, "octopus.otherprop=def"]);

        Assert.Null(result.Error);
        Assert.Equal(
            [new("octopus.prop", "abc"), new("octopus.otherprop", "def")],
            result.Options!.Attributes);
    }

    [Fact]
    public void AttributeValueMayContainEqualsAndBeEmpty()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "-a", "url=a=b", "-a", "empty="]);

        Assert.Null(result.Error);
        Assert.Equal([new("url", "a=b"), new("empty", "")], result.Options!.Attributes);
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("=novalue")]
    public void RejectsInvalidAttribute(string value)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "-a", value]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    [Fact]
    public void AttributesDefaultToEmpty()
    {
        var result = CommandLineParser.Parse(["traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.Empty(result.Options!.Attributes);
        Assert.False(result.Options.NoLogFileName);
    }

    [Theory]
    [InlineData("--http-header")]
    [InlineData("-H")]
    public void ParsesRepeatedHttpHeaders(string flag)
    {
        var result = CommandLineParser.Parse(
            ["traces.jsonl", flag, "X-Honeycomb-Team=hcik_123", flag, "X-Other=v"]);

        Assert.Null(result.Error);
        Assert.Equal(
            [new("X-Honeycomb-Team", "hcik_123"), new("X-Other", "v")],
            result.Options!.HttpHeaders);
    }

    [Fact]
    public void HttpHeaderValueMayContainEqualsAndBeEmpty()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "-H", "X-A=a=b", "-H", "X-Empty="]);

        Assert.Null(result.Error);
        Assert.Equal([new("X-A", "a=b"), new("X-Empty", "")], result.Options!.HttpHeaders);
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("=novalue")]
    public void RejectsInvalidHttpHeader(string value)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "-H", value]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    [Fact]
    public void HttpHeadersDefaultToEmpty()
    {
        var result = CommandLineParser.Parse(["traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.Empty(result.Options!.HttpHeaders);
    }

    [Fact]
    public void DashLowercaseHStillMeansHelp()
    {
        // -H is http-header; -h must remain help.
        var result = CommandLineParser.Parse(["-h"]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.ShowHelp);
    }

    [Fact]
    public void ParsesNoLogFileNameFlag()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--no-log-file-name"]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.NoLogFileName);
    }

    [Fact]
    public void ParsesFromAndToAsUtc()
    {
        var result = CommandLineParser.Parse(
            ["traces.jsonl", "--from", "2026-05-26T01:56:00Z", "--to", "2026-05-26T01:57:00"]);

        Assert.Null(result.Error);
        Assert.Equal(new DateTimeOffset(2026, 5, 26, 1, 56, 0, TimeSpan.Zero), result.Options!.From);
        // No offset -> treated as UTC.
        Assert.Equal(new DateTimeOffset(2026, 5, 26, 1, 57, 0, TimeSpan.Zero), result.Options.To);
    }

    [Fact]
    public void FromAndToDefaultToNull()
    {
        var result = CommandLineParser.Parse(["traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.Null(result.Options!.From);
        Assert.Null(result.Options.To);
    }

    [Theory]
    [InlineData("--from")]
    [InlineData("--to")]
    public void RejectsInvalidTimestamp(string flag)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag, "not-a-date"]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    [Fact]
    public void RejectsFromLaterThanTo()
    {
        var result = CommandLineParser.Parse(
            ["traces.jsonl", "--from", "2026-05-26T02:00:00Z", "--to", "2026-05-26T01:00:00Z"]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    [Fact]
    public void FlagsCanPrecedePositionalArgument()
    {
        var result = CommandLineParser.Parse(["-e", "http://host:4317", "-p", "grpc", "traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.Equal("traces.jsonl", result.Options!.InputFiles[0]);
        Assert.Equal("http://host:4317", result.Options.Endpoint);
        Assert.Equal(OtlpProtocol.Grpc, result.Options.Protocol);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void RecognisesHelp(string flag)
    {
        var result = CommandLineParser.Parse([flag]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.ShowHelp);
    }

    [Theory]
    [InlineData("--max-rate", "50", 50.0)]
    [InlineData("-r", "12.5", 12.5)]
    public void ParsesMaxRate(string flag, string value, double expected)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag, value]);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Options!.MaxBatchesPerSecond);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void RejectsInvalidMaxRate(string value)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--max-rate", value]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ParsesMaxRetries()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--max-retries", "0"]);

        Assert.Null(result.Error);
        Assert.Equal(0, result.Options!.MaxRetries);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("notanumber")]
    public void RejectsInvalidMaxRetries(string value)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--max-retries", value]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RejectsInvalidProtocol()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "-p", "carrier-pigeon"]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    [Fact]
    public void RejectsUnknownOption()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--frobnicate"]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RejectsMissingFlagValue()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--endpoint"]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ParsesMultiplePositionalArguments()
    {
        var result = CommandLineParser.Parse(["one.jsonl", "two.jsonl", "three/"]);

        Assert.Null(result.Error);
        Assert.Equal(["one.jsonl", "two.jsonl", "three/"], result.Options!.InputFiles);
    }
}
