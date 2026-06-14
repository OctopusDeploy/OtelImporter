using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using OtelImporter.Export;

namespace OtelImporter.Tests;

public class RetryingTraceExporterTests
{
    static readonly RetryOptions Options = new(MaxAttempts: 3, BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(30));
    static readonly ReadOnlyMemory<byte> Line = "{}"u8.ToArray();

    // Advances the fake clock repeatedly (with yields so continuations can register
    // their next delay) until the export task settles. Over-advancing is harmless;
    // these tests assert on attempt counts and outcomes, not exact timings.
    static async Task DriveAsync(Task task, FakeTimeProvider time)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            await Task.Yield();
            if (!task.IsCompleted)
                time.Advance(TimeSpan.FromSeconds(60)); // longer than MaxDelay, releases any backoff
        }
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var time = new FakeTimeProvider();
        var inner = new StubExporter(attempt => attempt <= 2
            ? throw new TraceExportException($"503 #{attempt}") { IsRetryable = true }
            : ExportOutcome.Accepted);
        var sut = new RetryingTraceExporter(inner, Options, time);

        var task = sut.ExportAsync(Line, CancellationToken.None);
        await DriveAsync(task, time);

        Assert.False(task.IsFaulted);
        Assert.Equal(3, inner.Attempts);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var time = new FakeTimeProvider();
        var inner = new StubExporter(attempt => throw new TraceExportException($"always #{attempt}") { IsRetryable = true });
        var sut = new RetryingTraceExporter(inner, Options, time);

        var task = sut.ExportAsync(Line, CancellationToken.None);
        await DriveAsync(task, time);

        await Assert.ThrowsAsync<TraceExportException>(() => task);
        Assert.Equal(Options.MaxAttempts, inner.Attempts);
    }

    [Fact]
    public async Task Does_not_retry_non_retryable_failures()
    {
        var time = new FakeTimeProvider();
        var inner = new StubExporter(_ => throw new TraceExportException("400 bad request") { IsRetryable = false });
        var sut = new RetryingTraceExporter(inner, Options, time);

        await Assert.ThrowsAsync<TraceExportException>(() => sut.ExportAsync(Line, CancellationToken.None));
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task Retries_network_errors()
    {
        var time = new FakeTimeProvider();
        var inner = new StubExporter(attempt => attempt == 1
            ? throw new HttpRequestException("connection refused")
            : ExportOutcome.Accepted);
        var sut = new RetryingTraceExporter(inner, Options, time);

        var task = sut.ExportAsync(Line, CancellationToken.None);
        await DriveAsync(task, time);

        await task;
        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task Does_not_retry_when_cancelled()
    {
        var time = new FakeTimeProvider();
        var inner = new StubExporter(_ => throw new TraceExportException("transient") { IsRetryable = true });
        var sut = new RetryingTraceExporter(inner, Options, time);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TraceExportException>(() => sut.ExportAsync(Line, cts.Token));
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task Honors_retry_after_hint_over_backoff()
    {
        var time = new FakeTimeProvider();
        var inner = new StubExporter(attempt => attempt == 1
            ? throw new TraceExportException("429") { IsRetryable = true, RetryAfter = TimeSpan.FromSeconds(10) }
            : ExportOutcome.Accepted);
        var sut = new RetryingTraceExporter(inner, Options, time);

        // The first delay timer is registered synchronously before ExportAsync yields.
        var task = sut.ExportAsync(Line, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(9));
        await Task.Yield();
        Assert.False(task.IsCompleted); // Retry-After is 10s, not the 1s backoff

        time.Advance(TimeSpan.FromSeconds(1)); // 10s total
        await task;
        Assert.Equal(2, inner.Attempts);
    }

    sealed class StubExporter(Func<int, ExportOutcome> onCall) : ITraceExporter
    {
        public int Attempts { get; private set; }

        public Task<ExportOutcome> ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(onCall(Attempts));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
