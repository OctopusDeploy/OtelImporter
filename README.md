# OtelImporter

A small, AOT-compiled .NET 10 console app that streams OpenTelemetry trace files
(`*.jsonl` / `*.jsonl.zst`) to an upstream OTLP endpoint (e.g. an OpenTelemetry
Collector) over **HTTP** or **gRPC**.

The input files are produced by our tests and are already in OTLP/JSON form (each
line is one `ExportTraceServiceRequest`).

## Usage

```
OtelImporter <input-file> [--endpoint <url>] [--protocol <grpc|http>]
```

| Argument / Option        | Description                                                        |
| ------------------------ | ----------------------------------------------------------------- |
| `<input-file>`           | Path to a `.jsonl` or `.jsonl.zst` OTLP trace file (positional).   |
| `-e`, `--endpoint <url>` | Upstream OTLP endpoint. Overrides the environment variables.       |
| `-p`, `--protocol <v>`   | `grpc` or `http`. Overrides the protocol sniffed from the port.    |
| `-h`, `--help`           | Show help.                                                         |

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
