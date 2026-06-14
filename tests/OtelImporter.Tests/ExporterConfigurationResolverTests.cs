using OtelImporter.Configuration;

namespace OtelImporter.Tests;

public class ExporterConfigurationResolverTests
{
    static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key);
    }

    static readonly Func<string, string?> NoEnv = _ => null;

    [Fact]
    public void CommandLineEndpointTakesPrecedenceOverEnvironment()
    {
        var options = new CommandLineOptions { Endpoint = "http://cli:4318", Protocol = OtlpProtocol.Http };
        var env = Env(
            (ExporterConfigurationResolver.TracesEndpointVariable, "http://traces-env:4318"),
            (ExporterConfigurationResolver.GenericEndpointVariable, "http://generic-env:4318"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Null(result.Error);
        Assert.Equal("cli", result.Configuration!.Endpoint.Host);
    }

    [Fact]
    public void TracesEnvironmentVariableTakesPrecedenceOverGeneric()
    {
        var options = new CommandLineOptions { Protocol = OtlpProtocol.Http };
        var env = Env(
            (ExporterConfigurationResolver.TracesEndpointVariable, "http://traces-env:4318"),
            (ExporterConfigurationResolver.GenericEndpointVariable, "http://generic-env:4318"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Null(result.Error);
        Assert.Equal("traces-env", result.Configuration!.Endpoint.Host);
    }

    [Fact]
    public void FallsBackToGenericEnvironmentVariable()
    {
        var options = new CommandLineOptions { Protocol = OtlpProtocol.Http };
        var env = Env((ExporterConfigurationResolver.GenericEndpointVariable, "http://generic-env:4318"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Null(result.Error);
        Assert.Equal("generic-env", result.Configuration!.Endpoint.Host);
    }

    [Fact]
    public void ErrorsWhenNoEndpointIsAvailable()
    {
        var result = ExporterConfigurationResolver.Resolve(new CommandLineOptions(), NoEnv);

        Assert.NotNull(result.Error);
        Assert.Null(result.Configuration);
    }

    [Theory]
    [InlineData("http://host:4317", OtlpProtocol.Grpc)]
    [InlineData("http://host:4318", OtlpProtocol.Http)]
    public void SniffsProtocolFromPort(string endpoint, OtlpProtocol expected)
    {
        var options = new CommandLineOptions { Endpoint = endpoint };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Configuration!.Protocol);
    }

    [Fact]
    public void ExplicitProtocolOverridesPortSniffing()
    {
        // Port says http, but --protocol grpc should win.
        var options = new CommandLineOptions { Endpoint = "http://host:4318", Protocol = OtlpProtocol.Grpc };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Null(result.Error);
        Assert.Equal(OtlpProtocol.Grpc, result.Configuration!.Protocol);
    }

    [Fact]
    public void ErrorsWhenProtocolCannotBeDetermined()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:9999" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.NotNull(result.Error);
        Assert.Contains("protocol", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HttpEndpointGetsTracesPathAppended()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal("http://host:4318/v1/traces", result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void HttpEndpointWithBasePathGetsTracesPathAppended()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318/otlp", Protocol = OtlpProtocol.Http };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal("http://host:4318/otlp/v1/traces", result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void HttpEndpointAlreadyPointingAtTracesPathIsLeftAlone()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318/v1/traces" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal("http://host:4318/v1/traces", result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void GrpcEndpointUsesFixedServicePath()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4317" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal(
            "http://host:4317/opentelemetry.proto.collector.trace.v1.TraceService/Export",
            result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void BareHostAndPortDefaultsToHttpScheme()
    {
        var options = new CommandLineOptions { Endpoint = "localhost:4317" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Null(result.Error);
        Assert.Equal(OtlpProtocol.Grpc, result.Configuration!.Protocol);
        Assert.Equal("localhost", result.Configuration.Endpoint.Host);
    }

    [Fact]
    public void HttpsGrpcEndpointIsPreserved()
    {
        var options = new CommandLineOptions { Endpoint = "https://host:4317" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal(Uri.UriSchemeHttps, result.Configuration!.Endpoint.Scheme);
    }
}
