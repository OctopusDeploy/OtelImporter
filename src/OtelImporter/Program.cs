using System.Diagnostics;
using OtelImporter.Configuration;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Inspect;
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

        var filter = SpanTimeFilter.Create(options.From, options.To);

        // --inspect is a read-only pass: no export, so endpoint/protocol/rate/retry are all ignored.
        if (options.Inspect)
            return await RunInspectAsync(options.InputFile!, options, filter);

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
        PrintTimeWindow(Console.Out, options);

        using var cancellation = new ConsoleCancellation();

        void ReportDiagnostic(string message)
        {
            // Clear the in-place progress line before writing a warning so it isn't overwritten.
            Console.Write("\r");
            Console.Error.WriteLine($"warning: {message}");
        }

        // Header values are often secrets (e.g. an API key), so log only the names.
        if (resolved.Headers.Count > 0)
            Console.WriteLine($"  http headers: {string.Join(", ", resolved.Headers.Select(h => h.Key))}");

        var factory = new ExporterFactory();
        var baseExporter = factory.Create(resolved, resolved.Headers);
        await using ITraceExporter exporter = retryOptions.MaxAttempts > 1
            ? new RetryingTraceExporter(baseExporter, retryOptions, TimeProvider.System, ReportDiagnostic)
            : baseExporter;

        IRateLimiter? rateLimiter = options.MaxBatchesPerSecond is { } batchesPerSecond
            ? new BatchRateLimiter(batchesPerSecond, TimeProvider.System)
            : null;

        var enricher = SpanEnricher.Create(
            options.NoLogFileName ? null : Path.GetFileName(options.InputFile),
            options.Attributes);
        if (enricher.HasAttributes)
        {
            Console.WriteLine("  adding span attributes:");
            foreach (var description in enricher.Describe())
                Console.WriteLine($"    {description}");
        }

        var runner = new ImportRunner(new InputStreamFactory(), exporter, rateLimiter, enricher, filter);

        // By default we also summarise what was exported; --no-inspect skips it.
        var inspector = options.NoInspect ? null : new TraceInspector();

        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<long>(count =>
        {
            if (count % 100 == 0)
                Console.Write($"\r  exported {count} batches...");
        });

        try
        {
            var result = await runner.RunAsync(options.InputFile, progress, ReportDiagnostic, inspector, cancellation.Token);
            stopwatch.Stop();
            Console.Write("\r");
            Console.WriteLine($"Done. Exported {result.BatchCount} batches in {stopwatch.Elapsed.TotalSeconds:F1}s.");

            if (inspector is not null)
                PrintSummary(inspector.BuildSummary(result.BatchCount));

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

    static async Task<int> RunInspectAsync(string inputFile, CommandLineOptions options, SpanTimeFilter? filter)
    {
        Console.WriteLine($"Inspecting '{inputFile}' (read-only, nothing will be exported)");
        PrintTimeWindow(Console.Out, options);

        using var cancellation = new ConsoleCancellation();

        var progress = new Progress<long>(count =>
        {
            if (count % 100 == 0)
                Console.Write($"\r  read {count} batches...");
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var runner = new InspectRunner(new InputStreamFactory(), filter);
            var summary = await runner.RunAsync(inputFile, progress, cancellation.Token);
            stopwatch.Stop();

            Console.Write("\r");
            Console.WriteLine($"Done. Read {summary.BatchCount} batches in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            PrintSummary(summary);
            return ExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Cancelled.");
            return ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCode.RuntimeError;
        }
    }

    static void PrintSummary(InspectionSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Batches:  {summary.BatchCount:N0}");
        Console.WriteLine($"  Spans:    {summary.SpanCount:N0}");

        if (summary is { OldestSpan: { } oldest, NewestSpan: { } newest, Duration: { } duration })
        {
            Console.WriteLine($"  Oldest:   {FormatTimestamp(oldest)}");
            Console.WriteLine($"  Newest:   {FormatTimestamp(newest)}");
            Console.WriteLine($"  Duration: {FormatDuration(duration)}");
        }
        else
        {
            Console.WriteLine("  Oldest:   n/a (no spans with a timestamp)");
            Console.WriteLine("  Newest:   n/a");
            Console.WriteLine("  Duration: n/a");
        }

        if (summary.TopSpanNames.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine($"  Top {summary.TopSpanNames.Count} span name(s) by count:");
        var countWidth = summary.TopSpanNames.Max(s => s.Count).ToString("N0").Length;
        foreach (var entry in summary.TopSpanNames)
            Console.WriteLine($"    {entry.Count.ToString("N0").PadLeft(countWidth)}  {entry.Name}");
    }

    static void PrintTimeWindow(TextWriter writer, CommandLineOptions options)
    {
        if (options.From is { } from)
            writer.WriteLine($"  from: {FormatTimestamp(from)} (earlier spans ignored)");
        if (options.To is { } to)
            writer.WriteLine($"  to:   {FormatTimestamp(to)} (later spans ignored)");
    }

    static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.FromSeconds(1)) return $"{d.TotalMilliseconds:N0}ms";
        if (d < TimeSpan.FromMinutes(1)) return $"{d.TotalSeconds:F2}s";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";
        return $"{(int)d.TotalDays}d {d.Hours}h {d.Minutes}m";
    }

    static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("OtelImporter - stream OpenTelemetry trace files to an OTLP endpoint.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  OtelImporter <input-file> [--endpoint <url>] [--protocol <grpc|http>]");
        writer.WriteLine("               [--max-rate <batches/sec>] [--max-retries <count>]");
        writer.WriteLine("  OtelImporter <input-file> --inspect");
        writer.WriteLine();
        writer.WriteLine("Arguments:");
        writer.WriteLine("  <input-file>            Path to a .jsonl or .jsonl.zst OTLP trace file.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -e, --endpoint <url>    Upstream OTLP endpoint. Overrides environment variables.");
        writer.WriteLine("  -p, --protocol <value>  'grpc' or 'http'. Overrides protocol sniffed from the port.");
        writer.WriteLine("  -r, --max-rate <n>      Throttle to at most n batches per second (default: unlimited).");
        writer.WriteLine("      --max-retries <n>   Retries per batch on transient failures (default: 4, 0 disables).");
        writer.WriteLine("  -i, --inspect           Read-only: summarise the file instead of exporting.");
        writer.WriteLine("                          Export options (endpoint, rate, retries) are ignored.");
        writer.WriteLine("      --no-inspect        Export without printing the end-of-run summary.");
        writer.WriteLine("  -a, --attribute k=v     Add an attribute to every exported span (repeatable).");
        writer.WriteLine("      --no-log-file-name  Do not add the automatic log.file.name attribute.");
        writer.WriteLine("  -H, --http-header k=v   Add an HTTP header to every export request (repeatable).");
        writer.WriteLine("      --from <datetime>   Ignore spans that start before this time (UTC if no offset).");
        writer.WriteLine("      --to <datetime>     Ignore spans that start after this time (UTC if no offset).");
        writer.WriteLine("  -h, --help              Show this help.");
        writer.WriteLine();
        writer.WriteLine("The command line always takes precedence over environment variables.");
        writer.WriteLine();
        writer.WriteLine("Endpoint resolution (highest precedence first):");
        writer.WriteLine("  1. --endpoint / -e");
        writer.WriteLine($"  2. {ExporterConfigurationResolver.TracesEndpointVariable}");
        writer.WriteLine($"  3. {ExporterConfigurationResolver.GenericEndpointVariable}");
        writer.WriteLine();
        writer.WriteLine("Protocol resolution (highest precedence first):");
        writer.WriteLine("  1. --protocol / -p");
        writer.WriteLine($"  2. {ExporterConfigurationResolver.TracesProtocolVariable}");
        writer.WriteLine($"  3. {ExporterConfigurationResolver.GenericProtocolVariable}");
        writer.WriteLine("  4. sniffed from the port (4317 => grpc, 4318 => http)");
        writer.WriteLine();
        writer.WriteLine("Headers are merged (command line wins on conflicts) from --http-header and:");
        writer.WriteLine($"  {ExporterConfigurationResolver.TracesHeadersVariable}");
        writer.WriteLine($"  {ExporterConfigurationResolver.GenericHeadersVariable}   (format: key1=value1,key2=value2)");
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
