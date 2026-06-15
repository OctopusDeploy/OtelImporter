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

    [Fact]
    public void EnvironmentVariableNamesMatchTheOtelSpec()
    {
        Assert.Equal("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", ExporterConfigurationResolver.TracesProtocolVariable);
        Assert.Equal("OTEL_EXPORTER_OTLP_PROTOCOL", ExporterConfigurationResolver.GenericProtocolVariable);
        Assert.Equal("OTEL_EXPORTER_OTLP_TRACES_HEADERS", ExporterConfigurationResolver.TracesHeadersVariable);
        Assert.Equal("OTEL_EXPORTER_OTLP_HEADERS", ExporterConfigurationResolver.GenericHeadersVariable);
    }

    // ---- Protocol from environment ----

    [Theory]
    [InlineData("grpc", OtlpProtocol.Grpc)]
    [InlineData("http/protobuf", OtlpProtocol.Http)]
    [InlineData("http/json", OtlpProtocol.Http)]
    public void ProtocolReadFromEnvironmentWhenNotOnCommandLine(string value, OtlpProtocol expected)
    {
        // Port 9999 can't be sniffed, so the env var is what resolves the protocol.
        var options = new CommandLineOptions { Endpoint = "http://host:9999" };
        var env = Env((ExporterConfigurationResolver.GenericProtocolVariable, value));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Configuration!.Protocol);
    }

    [Fact]
    public void TracesProtocolVariableTakesPrecedenceOverGeneric()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:9999" };
        var env = Env(
            (ExporterConfigurationResolver.TracesProtocolVariable, "grpc"),
            (ExporterConfigurationResolver.GenericProtocolVariable, "http/protobuf"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Equal(OtlpProtocol.Grpc, result.Configuration!.Protocol);
    }

    [Fact]
    public void CommandLineProtocolTakesPrecedenceOverEnvironment()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:9999", Protocol = OtlpProtocol.Http };
        var env = Env((ExporterConfigurationResolver.TracesProtocolVariable, "grpc"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Equal(OtlpProtocol.Http, result.Configuration!.Protocol);
    }

    [Fact]
    public void EnvironmentProtocolOverridesPortSniffing()
    {
        // Port says http (4318), but the env protocol grpc should win over sniffing.
        var options = new CommandLineOptions { Endpoint = "http://host:4318" };
        var env = Env((ExporterConfigurationResolver.GenericProtocolVariable, "grpc"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Equal(OtlpProtocol.Grpc, result.Configuration!.Protocol);
    }

    [Fact]
    public void ErrorsWhenEnvironmentProtocolIsInvalid()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:9999" };
        var env = Env((ExporterConfigurationResolver.GenericProtocolVariable, "carrier-pigeon"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.NotNull(result.Error);
        Assert.Contains("carrier-pigeon", result.Error);
    }

    // ---- Headers from environment ----

    static Dictionary<string, string> HeaderMap(ConfigurationResult result) =>
        result.Configuration!.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NoHeadersResolvesToEmpty()
    {
        var result = ExporterConfigurationResolver.Resolve(new CommandLineOptions { Endpoint = "http://host:4318" }, NoEnv);

        Assert.Empty(result.Configuration!.Headers);
    }

    [Fact]
    public void ParsesCommaSeparatedHeaderListFromEnvironment()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318" };
        var env = Env((ExporterConfigurationResolver.GenericHeadersVariable, "x-honeycomb-team=hcik_123, x-other=value"));

        var headers = HeaderMap(ExporterConfigurationResolver.Resolve(options, env));

        Assert.Equal("hcik_123", headers["x-honeycomb-team"]);
        Assert.Equal("value", headers["x-other"]);
    }

    [Fact]
    public void HeadersMergeAcrossSourcesWithCommandLineWinning()
    {
        var options = new CommandLineOptions
        {
            Endpoint = "http://host:4318",
            HttpHeaders = [new("X-Honeycomb-Team", "from-cli"), new("X-Cli-Only", "cli")],
        };
        var env = Env(
            (ExporterConfigurationResolver.GenericHeadersVariable, "x-honeycomb-team=from-generic,x-generic-only=g"),
            (ExporterConfigurationResolver.TracesHeadersVariable, "x-honeycomb-team=from-traces,x-traces-only=t"));

        var headers = HeaderMap(ExporterConfigurationResolver.Resolve(options, env));

        // Command line wins the shared key; traces beats generic; everything else merges in.
        Assert.Equal("from-cli", headers["x-honeycomb-team"]);
        Assert.Equal("g", headers["x-generic-only"]);
        Assert.Equal("t", headers["x-traces-only"]);
        Assert.Equal("cli", headers["x-cli-only"]);
    }

    [Fact]
    public void SkipsMalformedHeaderEntries()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318" };
        var env = Env((ExporterConfigurationResolver.GenericHeadersVariable, "good=1,,noequals,=novalue,also=2"));

        var headers = HeaderMap(ExporterConfigurationResolver.Resolve(options, env));

        Assert.Equal(["also", "good"], headers.Keys.OrderBy(k => k));
    }
}
