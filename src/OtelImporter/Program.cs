using System.Diagnostics;
using OtelImporter.Configuration;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Pipeline;

return await Importer.RunAsync(args);

internal static class Importer
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parse = CommandLineParser.Parse(args);
        if (parse.Error is not null)
        {
            Console.Error.WriteLine($"error: {parse.Error}");
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return ExitCode.UsageError;
        }

        var options = parse.Options!;
        if (options.ShowHelp)
        {
            PrintUsage(Console.Out);
            return ExitCode.Success;
        }

        if (string.IsNullOrWhiteSpace(options.InputFile))
        {
            Console.Error.WriteLine("error: no input file specified.");
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return ExitCode.UsageError;
        }

        if (!File.Exists(options.InputFile))
        {
            Console.Error.WriteLine($"error: input file not found: {options.InputFile}");
            return ExitCode.UsageError;
        }

        var configuration = ExporterConfigurationResolver.Resolve(options, Environment.GetEnvironmentVariable);
        if (configuration.Error is not null)
        {
            Console.Error.WriteLine($"error: {configuration.Error}");
            return ExitCode.UsageError;
        }

        var resolved = configuration.Configuration!;
        var retryOptions = RetryOptions.FromMaxRetries(options.MaxRetries ?? RetryOptions.Default.MaxAttempts - 1);

        Console.WriteLine($"Importing '{options.InputFile}'");
        Console.WriteLine($"  -> {resolved.Protocol.ToString().ToUpperInvariant()} {resolved.Endpoint}");
        if (options.MaxBatchesPerSecond is { } rate)
            Console.WriteLine($"  rate limit: {rate:G} batches/sec");
        Console.WriteLine($"  retries: up to {retryOptions.MaxAttempts - 1} per batch on transient failures");

        using var cancellation = new ConsoleCancellation();

        void ReportDiagnostic(string message)
        {
            // Clear the in-place progress line before writing a warning so it isn't overwritten.
            Console.Write("\r");
            Console.Error.WriteLine($"warning: {message}");
        }

        var factory = new ExporterFactory();
        var baseExporter = factory.Create(resolved);
        await using ITraceExporter exporter = retryOptions.MaxAttempts > 1
            ? new RetryingTraceExporter(baseExporter, retryOptions, TimeProvider.System, ReportDiagnostic)
            : baseExporter;

        IRateLimiter? rateLimiter = options.MaxBatchesPerSecond is { } batchesPerSecond
            ? new BatchRateLimiter(batchesPerSecond, TimeProvider.System)
            : null;

        var runner = new ImportRunner(new InputStreamFactory(), exporter, rateLimiter);

        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<long>(count =>
        {
            if (count % 100 == 0)
                Console.Write($"\r  exported {count} batches...");
        });

        try
        {
            var result = await runner.RunAsync(options.InputFile, progress, ReportDiagnostic, cancellation.Token);
            stopwatch.Stop();
            Console.Write("\r");
            Console.WriteLine($"Done. Exported {result.BatchCount} batches in {stopwatch.Elapsed.TotalSeconds:F1}s.");

            if (result.RejectedSpanCount > 0)
            {
                Console.Error.WriteLine(
                    $"WARNING: the collector rejected {result.RejectedSpanCount} span(s). " +
                    "They were accepted at the transport level but not stored upstream (see warnings above).");
                return ExitCode.PartialSuccess;
            }

            return ExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Cancelled.");
            return ExitCode.Cancelled;
        }
        catch (TraceExportException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"error: export failed: {ex.Message}");
            return ExitCode.RuntimeError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCode.RuntimeError;
        }
    }

    static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("OtelImporter - stream OpenTelemetry trace files to an OTLP endpoint.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  OtelImporter <input-file> [--endpoint <url>] [--protocol <grpc|http>]");
        writer.WriteLine("               [--max-rate <batches/sec>] [--max-retries <count>]");
        writer.WriteLine();
        writer.WriteLine("Arguments:");
        writer.WriteLine("  <input-file>            Path to a .jsonl or .jsonl.zst OTLP trace file.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -e, --endpoint <url>    Upstream OTLP endpoint. Overrides environment variables.");
        writer.WriteLine("  -p, --protocol <value>  'grpc' or 'http'. Overrides protocol sniffed from the port.");
        writer.WriteLine("  -r, --max-rate <n>      Throttle to at most n batches per second (default: unlimited).");
        writer.WriteLine("      --max-retries <n>   Retries per batch on transient failures (default: 4, 0 disables).");
        writer.WriteLine("  -h, --help              Show this help.");
        writer.WriteLine();
        writer.WriteLine("Endpoint resolution (highest precedence first):");
        writer.WriteLine("  1. --endpoint / -e");
        writer.WriteLine($"  2. {ExporterConfigurationResolver.TracesEndpointVariable}");
        writer.WriteLine($"  3. {ExporterConfigurationResolver.GenericEndpointVariable}");
        writer.WriteLine();
        writer.WriteLine("Protocol is sniffed from the port (4317 => grpc, 4318 => http) unless --protocol is given.");
    }
}

// Maps Ctrl+C / SIGTERM to a CancellationToken so an in-flight import stops cleanly.
internal sealed class ConsoleCancellation : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly ConsoleCancelEventHandler _handler;

    public ConsoleCancellation()
    {
        _handler = (_, e) =>
        {
            e.Cancel = true; // shut down gracefully instead of hard-killing
            _cts.Cancel();
        };
        Console.CancelKeyPress += _handler;
    }

    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= _handler;
        _cts.Dispose();
    }
}

internal static class ExitCode
{
    public const int Success = 0;
    public const int UsageError = 1;
    public const int RuntimeError = 2;
    public const int PartialSuccess = 3; // exported, but the collector rejected some spans
    public const int Cancelled = 130;
}
