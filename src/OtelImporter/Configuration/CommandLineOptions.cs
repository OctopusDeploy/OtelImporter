namespace OtelImporter.Configuration;

internal sealed record CommandLineOptions
{
    public string? InputFile { get; init; }
    public string? Endpoint { get; init; }
    public OtlpProtocol? Protocol { get; init; }
    public double? MaxBatchesPerSecond { get; init; }
    public int? MaxRetries { get; init; }
    public bool Inspect { get; init; }
    public bool NoInspect { get; init; }
    public bool NoLogFileName { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = [];
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
//   --max-rate, -r <value>  throttle: maximum batches per second
//   --max-retries <count>   retries per batch on transient failures (0 disables)
//   --inspect, -i           read-only: summarise the file instead of exporting
//   --no-inspect            export without printing the end-of-run summary
//   --attribute, -a k=v     add an attribute to every exported span (repeatable)
//   --no-log-file-name      do not add the automatic log.file.name attribute
//   --help, -h              show usage
internal static class CommandLineParser
{
    public static CommandLineParseResult Parse(string[] args)
    {
        string? inputFile = null;
        string? endpoint = null;
        OtlpProtocol? protocol = null;
        double? maxBatchesPerSecond = null;
        int? maxRetries = null;
        var inspect = false;
        var noInspect = false;
        var noLogFileName = false;
        var attributes = new List<KeyValuePair<string, string>>();

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

                case "--max-rate":
                case "-r":
                    if (!TryTakeValue(args, ref i, out var rateValue))
                        return CommandLineParseResult.Failure($"Missing value for {arg}.");
                    if (!double.TryParse(rateValue, System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate <= 0 || double.IsNaN(rate) || double.IsInfinity(rate))
                        return CommandLineParseResult.Failure($"Invalid value '{rateValue}' for {arg}. Expected a positive number of batches per second.");
                    maxBatchesPerSecond = rate;
                    break;

                case "--max-retries":
                    if (!TryTakeValue(args, ref i, out var retriesValue))
                        return CommandLineParseResult.Failure($"Missing value for {arg}.");
                    if (!int.TryParse(retriesValue, System.Globalization.CultureInfo.InvariantCulture, out var retries) || retries < 0)
                        return CommandLineParseResult.Failure($"Invalid value '{retriesValue}' for {arg}. Expected a non-negative integer.");
                    maxRetries = retries;
                    break;

                case "--inspect":
                case "-i":
                    inspect = true;
                    break;

                case "--no-inspect":
                    noInspect = true;
                    break;

                case "--no-log-file-name":
                    noLogFileName = true;
                    break;

                case "--attribute":
                case "-a":
                    if (!TryTakeValue(args, ref i, out var attributeValue))
                        return CommandLineParseResult.Failure($"Missing value for {arg}.");
                    var separator = attributeValue.IndexOf('=');
                    if (separator <= 0)
                        return CommandLineParseResult.Failure(
                            $"Invalid attribute '{attributeValue}' for {arg}. Expected name=value.");
                    attributes.Add(new KeyValuePair<string, string>(
                        attributeValue[..separator], attributeValue[(separator + 1)..]));
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

        if (inspect && noInspect)
            return CommandLineParseResult.Failure("Cannot combine --inspect (read-only) with --no-inspect.");

        return CommandLineParseResult.Success(new CommandLineOptions
        {
            InputFile = inputFile,
            Endpoint = endpoint,
            Protocol = protocol,
            MaxBatchesPerSecond = maxBatchesPerSecond,
            MaxRetries = maxRetries,
            Inspect = inspect,
            NoInspect = noInspect,
            NoLogFileName = noLogFileName,
            Attributes = attributes,
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
