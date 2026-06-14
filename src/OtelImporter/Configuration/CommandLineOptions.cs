namespace OtelImporter.Configuration;

internal sealed record CommandLineOptions
{
    public string? InputFile { get; init; }
    public string? Endpoint { get; init; }
    public OtlpProtocol? Protocol { get; init; }
    public bool ShowHelp { get; init; }
}

internal sealed record CommandLineParseResult(CommandLineOptions? Options, string? Error)
{
    public static CommandLineParseResult Success(CommandLineOptions options) => new(options, null);
    public static CommandLineParseResult Failure(string error) => new(null, error);
}

// Hand-rolled argument parsing keeps the dependency surface (and AOT footprint) small.
// Supported:
//   <input-file>            positional, the *.jsonl or *.jsonl.zst trace file
//   --endpoint, -e <url>    upstream OTLP endpoint (overrides environment variables)
//   --protocol, -p <value>  grpc | http (overrides port sniffing)
//   --help, -h              show usage
internal static class CommandLineParser
{
    public static CommandLineParseResult Parse(string[] args)
    {
        string? inputFile = null;
        string? endpoint = null;
        OtlpProtocol? protocol = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    return CommandLineParseResult.Success(new CommandLineOptions { ShowHelp = true });

                case "--endpoint":
                case "-e":
                    if (!TryTakeValue(args, ref i, out var endpointValue))
                        return CommandLineParseResult.Failure($"Missing value for {arg}.");
                    endpoint = endpointValue;
                    break;

                case "--protocol":
                case "-p":
                    if (!TryTakeValue(args, ref i, out var protocolValue))
                        return CommandLineParseResult.Failure($"Missing value for {arg}.");
                    if (!TryParseProtocol(protocolValue, out var parsedProtocol))
                        return CommandLineParseResult.Failure($"Invalid protocol '{protocolValue}'. Expected 'grpc' or 'http'.");
                    protocol = parsedProtocol;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return CommandLineParseResult.Failure($"Unknown option '{arg}'.");
                    if (inputFile is not null)
                        return CommandLineParseResult.Failure($"Unexpected extra argument '{arg}'. Only one input file is supported.");
                    inputFile = arg;
                    break;
            }
        }

        return CommandLineParseResult.Success(new CommandLineOptions
        {
            InputFile = inputFile,
            Endpoint = endpoint,
            Protocol = protocol,
        });
    }

    static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    public static bool TryParseProtocol(string value, out OtlpProtocol protocol)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "grpc":
                protocol = OtlpProtocol.Grpc;
                return true;
            case "http":
            case "http/protobuf":
            case "http/json":
                protocol = OtlpProtocol.Http;
                return true;
            default:
                protocol = default;
                return false;
        }
    }
}
