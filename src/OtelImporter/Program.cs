using System.Diagnostics;
using OtelImporter.Configuration;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Inspect;
using OtelImporter.Pipeline;

return await Importer.RunAsync(args);

internal static class Importer
{
    // Stop a multi-file run once this many files fail back-to-back: a streak of failures
    // points at the upstream being down rather than a few individually bad files.
    const int MaxConsecutiveFailures = 3;

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

        // The positional argument may be a single file or a directory of trace files.
        var resolution = InputResolver.Resolve(options.InputFile!);
        if (resolution.Error is not null)
        {
            Console.Error.WriteLine($"error: {resolution.Error}");
            return ExitCode.UsageError;
        }

        var inputFiles = resolution.Files;
        var filter = SpanTimeFilter.Create(options.From, options.To);

        // --inspect is a read-only pass: no export, so endpoint/protocol/rate/retry are all ignored.
        if (options.Inspect)
            return await RunInspectAsync(inputFiles, options, filter);

        return await RunExportAsync(inputFiles, options, filter);
    }

    static async Task<int> RunExportAsync(IReadOnlyList<string> inputFiles, CommandLineOptions options, SpanTimeFilter? filter)
    {
        var configuration = ExporterConfigurationResolver.Resolve(options, Environment.GetEnvironmentVariable);
        if (configuration.Error is not null)
        {
            Console.Error.WriteLine($"error: {configuration.Error}");
            return ExitCode.UsageError;
        }

        var resolved = configuration.Configuration!;
        var retryOptions = RetryOptions.FromMaxRetries(options.MaxRetries ?? RetryOptions.Default.MaxAttempts - 1);

        DescribeInputs("Importing", inputFiles);
        Console.WriteLine($"  -> {resolved.Protocol.ToString().ToUpperInvariant()} {resolved.Endpoint}");
        if (options.MaxBatchesPerSecond is { } rate)
            Console.WriteLine($"  rate limit: {rate:G} batches/sec");
        Console.WriteLine($"  retries: up to {retryOptions.MaxAttempts - 1} per batch on transient failures");
        if (options.MaxBatchSizeKb is { } maxBatchKb)
            Console.WriteLine($"  max batch size: {maxBatchKb} KB (larger batches are split by span)");
        PrintTimeWindow(Console.Out, options);

        // KB on the command line, bytes for measuring; null means no splitting.
        long? maxBatchBytes = options.MaxBatchSizeKb is { } kb ? kb * 1024L : null;

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
        var baseExporter = factory.Create(resolved, resolved.Headers, maxBatchBytes);
        await using ITraceExporter exporter = retryOptions.MaxAttempts > 1
            ? new RetryingTraceExporter(baseExporter, retryOptions, TimeProvider.System, ReportDiagnostic)
            : baseExporter;

        IRateLimiter? rateLimiter = options.MaxBatchesPerSecond is { } batchesPerSecond
            ? new BatchRateLimiter(batchesPerSecond, TimeProvider.System)
            : null;

        // log.file.name is enriched per file (each gets its own name); --attribute values
        // are the same everywhere. Describe both up front rather than once per file. With a
        // single file we can show the concrete name; a directory gets a generic note since
        // the value differs per file.
        if (!options.NoLogFileName)
            Console.WriteLine(inputFiles.Count == 1
                ? $"  adding attribute: log.file.name={Path.GetFileName(inputFiles[0])}"
                : "  adding attribute: log.file.name (set to each file's name)");
        if (options.Attributes.Count > 0)
        {
            Console.WriteLine("  adding span attributes:");
            foreach (var (key, value) in options.Attributes)
                Console.WriteLine($"    {key}={value}");
        }

        var inputStreamFactory = new InputStreamFactory();

        // By default we also summarise what was exported; --no-inspect skips it. One
        // inspector spans every file so the closing summary covers the whole import.
        var inspector = options.NoInspect ? null : new TraceInspector();

        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<long>(count =>
        {
            if (count % 100 == 0)
                Console.Write($"\r  exported {count} batches...");
        });

        try
        {
            var batchCount = 0L;
            var rejectedSpanCount = 0L;
            var skippedSpanCount = 0L;
            var failedFiles = new List<string>();
            var consecutiveFailures = 0;
            var aborted = false;
            foreach (var inputFile in inputFiles)
            {
                // A fresh enricher per file so log.file.name reflects the file in hand.
                var enricher = SpanEnricher.Create(
                    options.NoLogFileName ? null : Path.GetFileName(inputFile),
                    options.Attributes);
                var runner = new ImportRunner(inputStreamFactory, exporter, rateLimiter, enricher, filter);

                try
                {
                    var result = await runner.RunAsync(
                        inputFile, progress, ReportDiagnostic, inspector, cancellation.Token, batchCount);
                    batchCount = result.BatchCount;
                    rejectedSpanCount += result.RejectedSpanCount;
                    skippedSpanCount += result.SkippedSpanCount;
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.Token.IsCancellationRequested)
                {
                    // Isolate per-file failures: note the file and carry on, so one bad
                    // file in a directory doesn't sink the rest of the import. Cancellation
                    // is excluded above so Ctrl+C still aborts the whole run.
                    Console.Write("\r");
                    Console.Error.WriteLine($"warning: failed to import '{inputFile}': {ex.Message}");
                    failedFiles.Add(inputFile);

                    // A run of failures usually means the upstream is down rather than a few
                    // bad files, so stop rather than churn through the rest of the directory.
                    if (++consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        aborted = true;
                        break;
                    }
                }
            }

            stopwatch.Stop();
            Console.Write("\r");
            Console.WriteLine($"Done. Exported {batchCount} batches in {stopwatch.Elapsed.TotalSeconds:F1}s.");

            if (inspector is not null)
                PrintSummary(inspector.BuildSummary(batchCount));

            if (rejectedSpanCount > 0)
                Console.Error.WriteLine(
                    $"WARNING: the collector rejected {rejectedSpanCount} span(s). " +
                    "They were accepted at the transport level but not stored upstream (see warnings above).");

            if (skippedSpanCount > 0)
                Console.Error.WriteLine(
                    $"WARNING: skipped {skippedSpanCount} span(s) larger than the {options.MaxBatchSizeKb} KB " +
                    "max batch size; they could not be split small enough and were not sent.");

            if (failedFiles.Count > 0)
            {
                // List the specific files that failed -- only the failures, so the output
                // stays small even when a directory holds hundreds of files.
                Console.Error.WriteLine(aborted
                    ? $"ERROR: aborted after {MaxConsecutiveFailures} consecutive failures; {failedFiles.Count} of {inputFiles.Count} file(s) failed to import:"
                    : $"WARNING: {failedFiles.Count} of {inputFiles.Count} file(s) failed to import:");
                foreach (var file in failedFiles)
                    Console.Error.WriteLine($"  {file}");

                // An aborted run, or one where nothing succeeded, is a hard failure;
                // otherwise some files made it through, so it's a partial success.
                return aborted || failedFiles.Count == inputFiles.Count
                    ? ExitCode.RuntimeError
                    : ExitCode.PartialSuccess;
            }

            // Rejected or skipped spans mean not everything landed upstream -> partial success.
            return rejectedSpanCount > 0 || skippedSpanCount > 0 ? ExitCode.PartialSuccess : ExitCode.Success;
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

    static async Task<int> RunInspectAsync(IReadOnlyList<string> inputFiles, CommandLineOptions options, SpanTimeFilter? filter)
    {
        DescribeInputs("Inspecting", inputFiles, " (read-only, nothing will be exported)");
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
            // One inspector and a running batch count fold every file into a single summary.
            var runner = new InspectRunner(new InputStreamFactory(), filter);
            var inspector = new TraceInspector();
            var batchCount = 0L;
            var failedFiles = new List<string>();
            var consecutiveFailures = 0;
            var aborted = false;
            foreach (var inputFile in inputFiles)
            {
                try
                {
                    batchCount = await runner.RunAsync(inputFile, inspector, progress, batchCount, cancellation.Token);
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.Token.IsCancellationRequested)
                {
                    // Skip a bad file and keep going so one unreadable file doesn't sink the
                    // whole inspection. Cancellation is excluded so Ctrl+C still stops the run.
                    Console.Write("\r");
                    Console.Error.WriteLine($"warning: failed to read '{inputFile}': {ex.Message}");
                    failedFiles.Add(inputFile);

                    if (++consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        aborted = true;
                        break;
                    }
                }
            }

            stopwatch.Stop();
            Console.Write("\r");
            Console.WriteLine($"Done. Read {batchCount} batches in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            PrintSummary(inspector.BuildSummary(batchCount));

            if (failedFiles.Count > 0)
            {
                Console.Error.WriteLine(aborted
                    ? $"ERROR: aborted after {MaxConsecutiveFailures} consecutive failures; {failedFiles.Count} of {inputFiles.Count} file(s) failed to read:"
                    : $"WARNING: {failedFiles.Count} of {inputFiles.Count} file(s) failed to read:");
                foreach (var file in failedFiles)
                    Console.Error.WriteLine($"  {file}");

                return aborted || failedFiles.Count == inputFiles.Count
                    ? ExitCode.RuntimeError
                    : ExitCode.PartialSuccess;
            }

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

    // "Importing 'traces.jsonl'" for a single file, or just the file count when a directory
    // resolved to several -- listing every file is too noisy for large directories. The verb
    // ("Importing"/"Inspecting") and an optional suffix keep the line consistent with the banner.
    static void DescribeInputs(string verb, IReadOnlyList<string> inputFiles, string suffix = "")
    {
        if (inputFiles.Count == 1)
        {
            Console.WriteLine($"{verb} '{inputFiles[0]}'{suffix}");
            return;
        }

        Console.WriteLine($"{verb} {inputFiles.Count} files{suffix}.");
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
        writer.WriteLine("  OtelImporter <input> [--endpoint <url>] [--protocol <grpc|http>]");
        writer.WriteLine("               [--max-rate <batches/sec>] [--max-retries <count>] [--max-batch-size <kb>]");
        writer.WriteLine("  OtelImporter <input> --inspect");
        writer.WriteLine();
        writer.WriteLine("Arguments:");
        writer.WriteLine("  <input>                 Path to a .jsonl, .jsonl.zst or .json OTLP trace file, or a");
        writer.WriteLine("                          directory; every .jsonl/.jsonl.zst/.json file directly inside");
        writer.WriteLine("                          it is processed in name order.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -e, --endpoint <url>    Upstream OTLP endpoint. Overrides environment variables.");
        writer.WriteLine("  -p, --protocol <value>  'grpc' or 'http'. Overrides protocol sniffed from the port.");
        writer.WriteLine("  -r, --max-rate <n>      Throttle to at most n batches per second (default: unlimited).");
        writer.WriteLine("      --max-retries <n>   Retries per batch on transient failures (default: 4, 0 disables).");
        writer.WriteLine("      --max-batch-size <kb>  Split batches larger than this many KB into smaller ones");
        writer.WriteLine("                          before sending (by span; default: no splitting). Export only.");
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
