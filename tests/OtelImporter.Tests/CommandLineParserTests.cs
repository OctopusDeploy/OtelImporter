using OtelImporter.Configuration;

namespace OtelImporter.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void ParsesPositionalInputFile()
    {
        var result = CommandLineParser.Parse(["traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.Equal("traces.jsonl", result.Options!.InputFile);
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
        Assert.Equal("traces.jsonl", result.Options.InputFile);
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

    [Fact]
    public void FlagsCanPrecedePositionalArgument()
    {
        var result = CommandLineParser.Parse(["-e", "http://host:4317", "-p", "grpc", "traces.jsonl"]);

        Assert.Null(result.Error);
        Assert.Equal("traces.jsonl", result.Options!.InputFile);
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
    public void RejectsTwoPositionalArguments()
    {
        var result = CommandLineParser.Parse(["one.jsonl", "two.jsonl"]);

        Assert.NotNull(result.Error);
    }
}
