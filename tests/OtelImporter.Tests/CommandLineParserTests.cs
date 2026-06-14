using OtelImporter.Configuration;

namespace OtelImporter.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void Parses_positional_input_file()
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
    public void Parses_endpoint_flag(string flag)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag, "http://localhost:4318"]);

        Assert.Null(result.Error);
        Assert.Equal("http://localhost:4318", result.Options!.Endpoint);
    }

    [Theory]
    [InlineData("--protocol", "grpc", OtlpProtocol.Grpc)]
    [InlineData("-p", "http", OtlpProtocol.Http)]
    [InlineData("-p", "GRPC", OtlpProtocol.Grpc)]
    public void Parses_protocol_flag(string flag, string value, OtlpProtocol expected)
    {
        var result = CommandLineParser.Parse(["traces.jsonl", flag, value]);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Options!.Protocol);
    }

    [Fact]
    public void Flags_can_precede_positional_argument()
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
    public void Recognises_help(string flag)
    {
        var result = CommandLineParser.Parse([flag]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.ShowHelp);
    }

    [Fact]
    public void Rejects_invalid_protocol()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "-p", "carrier-pigeon"]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    [Fact]
    public void Rejects_unknown_option()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--frobnicate"]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Rejects_missing_flag_value()
    {
        var result = CommandLineParser.Parse(["traces.jsonl", "--endpoint"]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Rejects_two_positional_arguments()
    {
        var result = CommandLineParser.Parse(["one.jsonl", "two.jsonl"]);

        Assert.NotNull(result.Error);
    }
}
