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
    public void Command_line_endpoint_takes_precedence_over_environment()
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
    public void Traces_environment_variable_takes_precedence_over_generic()
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
    public void Falls_back_to_generic_environment_variable()
    {
        var options = new CommandLineOptions { Protocol = OtlpProtocol.Http };
        var env = Env((ExporterConfigurationResolver.GenericEndpointVariable, "http://generic-env:4318"));

        var result = ExporterConfigurationResolver.Resolve(options, env);

        Assert.Null(result.Error);
        Assert.Equal("generic-env", result.Configuration!.Endpoint.Host);
    }

    [Fact]
    public void Errors_when_no_endpoint_is_available()
    {
        var result = ExporterConfigurationResolver.Resolve(new CommandLineOptions(), NoEnv);

        Assert.NotNull(result.Error);
        Assert.Null(result.Configuration);
    }

    [Theory]
    [InlineData("http://host:4317", OtlpProtocol.Grpc)]
    [InlineData("http://host:4318", OtlpProtocol.Http)]
    public void Sniffs_protocol_from_port(string endpoint, OtlpProtocol expected)
    {
        var options = new CommandLineOptions { Endpoint = endpoint };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Configuration!.Protocol);
    }

    [Fact]
    public void Explicit_protocol_overrides_port_sniffing()
    {
        // Port says http, but --protocol grpc should win.
        var options = new CommandLineOptions { Endpoint = "http://host:4318", Protocol = OtlpProtocol.Grpc };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Null(result.Error);
        Assert.Equal(OtlpProtocol.Grpc, result.Configuration!.Protocol);
    }

    [Fact]
    public void Errors_when_protocol_cannot_be_determined()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:9999" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.NotNull(result.Error);
        Assert.Contains("protocol", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Http_endpoint_gets_traces_path_appended()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal("http://host:4318/v1/traces", result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void Http_endpoint_with_base_path_gets_traces_path_appended()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318/otlp", Protocol = OtlpProtocol.Http };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal("http://host:4318/otlp/v1/traces", result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void Http_endpoint_already_pointing_at_traces_path_is_left_alone()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4318/v1/traces" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal("http://host:4318/v1/traces", result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void Grpc_endpoint_uses_fixed_service_path()
    {
        var options = new CommandLineOptions { Endpoint = "http://host:4317" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal(
            "http://host:4317/opentelemetry.proto.collector.trace.v1.TraceService/Export",
            result.Configuration!.Endpoint.ToString());
    }

    [Fact]
    public void Bare_host_and_port_defaults_to_http_scheme()
    {
        var options = new CommandLineOptions { Endpoint = "localhost:4317" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Null(result.Error);
        Assert.Equal(OtlpProtocol.Grpc, result.Configuration!.Protocol);
        Assert.Equal("localhost", result.Configuration.Endpoint.Host);
    }

    [Fact]
    public void Https_grpc_endpoint_is_preserved()
    {
        var options = new CommandLineOptions { Endpoint = "https://host:4317" };

        var result = ExporterConfigurationResolver.Resolve(options, NoEnv);

        Assert.Equal(Uri.UriSchemeHttps, result.Configuration!.Endpoint.Scheme);
    }
}
