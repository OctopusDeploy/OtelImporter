# OtelImporter

A small, AOT-compiled .NET 10 console app that streams OpenTelemetry trace files
(`*.jsonl` / `*.jsonl.zst`) to an upstream OTLP endpoint (e.g. an OpenTelemetry
Collector) over **HTTP** or **gRPC**.

The input files are produced by our tests and are already in OTLP/JSON form (each
line is one `ExportTraceServiceRequest`).

## Usage

```
OtelImporter <input-file> [--endpoint <url>] [--protocol <grpc|http>]
OtelImporter <input-file> --inspect
```

| Argument / Option        | Description                                                        |
| ------------------------ | ----------------------------------------------------------------- |
| `<input-file>`           | Path to a `.jsonl` or `.jsonl.zst` OTLP trace file (positional).   |
| `-e`, `--endpoint <url>` | Upstream OTLP endpoint. Overrides the environment variables.       |
| `-p`, `--protocol <v>`   | `grpc` or `http`. Overrides the protocol sniffed from the port.    |
| `-r`, `--max-rate <n>`   | Throttle to at most `n` batches/sec (default: unlimited).          |
| `--max-retries <n>`      | Retries per batch on transient failures (default: 4, `0` disables).|
| `-i`, `--inspect`        | Read-only: summarise the file instead of exporting (see below).    |
| `--no-inspect`           | Export without printing the end-of-run summary.                    |
| `-h`, `--help`           | Show help.                                                         |

Each line of the input file is one batch (one `ExportTraceServiceRequest`).

### Endpoint resolution (highest precedence first)

1. `--endpoint` / `-e`
2. `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`
3. `OTEL_EXPORTER_OTLP_ENDPOINT`

### Protocol resolution

The protocol is sniffed from the endpoint port — **4317 ⇒ gRPC**, **4318 ⇒ HTTP** —
unless `--protocol` is given. If the port is neither and `--protocol` is omitted,
the app errors and asks you to specify one.

For HTTP, the `/v1/traces` signal path is appended automatically if not already
present. For gRPC, the standard `TraceService/Export` method path is used.

### Examples

```bash
# Auto-sniffed gRPC (port 4317)
OtelImporter traces.jsonl.zst -e http://collector:4317

# HTTP, explicit protocol
OtelImporter traces.jsonl --endpoint http://collector:8080 --protocol http

# Endpoint from the environment
export OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4318
OtelImporter traces.jsonl.zst
```

## Inspecting a file

A normal export **also prints the summary below** when it finishes — the file is parsed
once and fed to both the exporter and the inspector, so there's no extra pass. Use
`--no-inspect` for a bare export with no summary.

`--inspect` (`-i`) does the same summary as a *read-only* pass: nothing is exported. All
export options (`--endpoint`, `--protocol`, `--max-rate`, `--max-retries`) and the
endpoint environment variables are ignored, so no upstream collector is needed.

```bash
OtelImporter traces.jsonl.zst --inspect             # summary only, no export
OtelImporter traces.jsonl.zst -e <url>              # export, then summary
OtelImporter traces.jsonl.zst -e <url> --no-inspect # export, no summary
```

```
Summary:
  Batches:  4
  Spans:    139
  Oldest:   2026-05-26 01:55:21.808 UTC
  Newest:   2026-05-26 01:57:03.225 UTC
  Duration: 1m 41s

  Top 10 span name(s) by count:
    27  EF SQL
    24  SQL
    ...
```

The summary reports the batch and span counts, the oldest/newest span start times and
the wall-clock span between them, and the ten most common span names (grouped by the
span `name`; nameless spans are counted as `<No Name>`). Everything is accumulated
incrementally as the file streams — individual spans are never buffered, so the memory
profile is the same as a normal import regardless of file size.

## Reliability

- **Partial success surfacing.** A collector can return HTTP 2xx / gRPC OK and still
  reject spans via `partial_success`. The importer parses the response (JSON for HTTP,
  protobuf for gRPC) and prints a warning per affected batch plus a final total. If any
  spans were rejected it exits **3** so the condition is scriptable.
- **Retry with backoff.** Transient failures (HTTP `408/429/502/503/504`, gRPC
  `UNAVAILABLE`/`RESOURCE_EXHAUSTED`, and network/timeout errors) are retried with
  exponential backoff (honouring `Retry-After`). Tune with `--max-retries`.
- **Rate limiting.** A bulk import can overrun a collector's export queue sized for
  steady-state traffic (spans get dropped downstream, *after* an OK response). Use
  `--max-rate` to pace sending.

### Exit codes

| Code | Meaning                                                        |
| ---- | ------------------------------------------------------------- |
| 0    | Success.                                                       |
| 1    | Usage error (bad arguments, missing file).                    |
| 2    | Runtime error (export failed after retries).                  |
| 3    | Exported, but the collector rejected some spans.              |
| 130  | Cancelled (Ctrl+C).                                           |

## Design notes

- **Streaming everywhere.** Input is read line-by-line at the byte level; `.zst`
  files are decompressed through a streaming decompressor. An 800 MB-decompressed
  file imports with a ~45 MB peak working set.
- **Minimal dependencies.** The only runtime dependency is
  [`ZstdSharp.Port`](https://www.nuget.org/packages/ZstdSharp.Port) (a pure-managed,
  AOT-friendly zstd port). JSON uses `System.Text.Json` source generation.
- **No gRPC tooling at runtime.** gRPC is spoken directly over `HttpClient`
  (HTTP/2 framing + manual OTLP protobuf encoding), avoiding `Grpc.Net.Client`.
- **HTTP forwards verbatim.** Because the input is already OTLP/JSON, the HTTP
  exporter posts each line as-is to `/v1/traces` — no parse/re-serialize.
- **No IoC container.** Dependencies are constructed directly in `Program.cs`;
  interfaces exist only to keep the pieces unit-testable.

## Layout

```
src/OtelImporter/              the app
  Configuration/               CLI parsing + endpoint/protocol resolution
  Input/                       streaming file open (zstd sniff) + JSONL line reader
  Otlp/                        OTLP object model, JSON source-gen, protobuf writer
  Export/                      HTTP + gRPC exporters
  Pipeline/                    ImportRunner (orchestration)
tests/OtelImporter.Tests/             unit tests (parsing, protobuf, streaming, ...)
tests/OtelImporter.IntegrationTests/  e2e: real ASP.NET Core OTLP receiver (gRPC + HTTP)
```

## Build, test, publish

```bash
dotnet test                                   # unit + integration tests
dotnet publish src/OtelImporter -c Release    # native AOT executable
```
