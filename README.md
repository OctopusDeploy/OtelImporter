# OtelImporter

A small, AOT-compiled .NET 10 console app that streams OpenTelemetry trace files
(`*.jsonl` / `*.jsonl.zst` / `*.json`) to an upstream OTLP endpoint (e.g. an OpenTelemetry
Collector) over **HTTP** or **gRPC**.

## Usage / Examples

### Upload a trace file to your local Otel Collector or Jaeger/SigNoz instance

```bash
OtelImporter traces-1776.jsonl.zst --endpoint http://localhost:4318 --max-rate 10
```

_Note:_ When running through a local OpenTelemetry collector, it is likely to silently drop spans that exceed the rate-limit.
If you are sending data directly to jaeger or signoz, you may not need `--max-rate` or a higher rate may be fine.

### Upload a whole directory of trace files

Point the importer at a directory and it processes every `.jsonl` / `.jsonl.zst` / `.json`
file directly inside it (in name order), through the one connection. Subdirectories are not
searched. The end-of-run summary covers all files combined, and each file's spans get a
`log.file.name` attribute set to that file's name.

If a file fails to import, the run carries on with the remaining files and lists the ones
that failed at the end. The exit code is then `3` (partial success) if some files still
succeeded, or `2` if every file failed. If three files in a row fail — usually a sign the
upstream is down rather than a few bad files — the run stops early and exits with code `2`.

```bash
OtelImporter ./traces --endpoint http://localhost:4318 --max-rate 10
```

### Split oversized batches

Some upstreams reject batches over a size limit (for example gRPC's default 4 MB message
limit). Pass `--max-batch-size <kb>` and any batch larger than that is broken into several
smaller batches — split by span, preserving each span's resource/scope — as it is written
to the wire. The size is measured in the format actually sent (protobuf for gRPC, JSON for
HTTP), so the same limit yields more, smaller batches over HTTP than over the more compact
gRPC. A single span that on its own exceeds the limit can't be split, so it is skipped
(with a warning) and the run finishes with exit code `3`.

```bash
OtelImporter ./traces.json --endpoint http://localhost:4318 --max-batch-size 512
```

`--max-batch-size` only affects exporting; `--inspect` ignores it (each input line counts
as one batch).

### Upload a trace file to Honeycomb

```bash
OtelImporter octopus-server-traces-2026-06-15T02-05-55.jsonl.zst --endpoint https://api.honeycomb.io  -p grpc --http-header X-Honeycomb-Team=hcaik_YOUR_INGEST_KEY_HERE
```

_Note:_ Uploading large blocks of data to Honeycomb can take a while. If you are investigating a particular issue, use the `--from` and `--to` parameters to filter the time range
and reduce the amount of data uploaded.

### Inspect a trace file

```bash
OtelImporter traces-1776.jsonl.zst --inspect
```

## Detailed Usage

| Argument / Option        | Description                                                        |
| ------------------------ | ----------------------------------------------------------------- |
| `<input>`                | Path to a `.jsonl`/`.jsonl.zst`/`.json` trace file, or a directory of them (positional). |
| `-e`, `--endpoint <url>` | Upstream OTLP endpoint. Overrides the environment variables.       |
| `-p`, `--protocol <v>`   | `grpc` or `http`. Overrides the protocol sniffed from the port.    |
| `-r`, `--max-rate <n>`   | Throttle to at most `n` batches/sec (default: unlimited).          |
| `--max-retries <n>`      | Retries per batch on transient failures (default: 4, `0` disables).|
| `--max-batch-size <kb>`  | Split batches larger than `n` KB into smaller ones as they're sent (default: off). Export only; ignored by `--inspect`. |
| `-i`, `--inspect`        | Read-only: summarise the file instead of exporting (see below).    |
| `--no-inspect`           | Export without printing the end-of-run summary.                    |
| `-a`, `--attribute k=v`  | Add an attribute to every exported span. Repeatable.              |
| `--no-log-file-name`     | Don't add the automatic `log.file.name` attribute.                |
| `-H`, `--http-header k=v`| Add an HTTP header to every export request. Repeatable.           |
| `--from <datetime>`      | Ignore spans that start before this time (UTC if no offset).       |
| `--to <datetime>`        | Ignore spans that start after this time (UTC if no offset).        |
| `-h`, `--help`           | Show help.                                                         |

Each line of the input file is one batch (one `ExportTraceServiceRequest`).

The endpoint, protocol and headers can each come from the command line or the standard
OpenTelemetry environment variables. **The command line always takes precedence**, and
signal-specific (`..._TRACES_...`) variables take precedence over the generic ones.

### Endpoint resolution (highest precedence first)

1. `--endpoint` / `-e`
2. `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`
3. `OTEL_EXPORTER_OTLP_ENDPOINT`

### Protocol resolution (highest precedence first)

1. `--protocol` / `-p`
2. `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL`
3. `OTEL_EXPORTER_OTLP_PROTOCOL`
4. Sniffed from the endpoint port — **4317 ⇒ gRPC**, **4318 ⇒ HTTP**.

If none of these determine the protocol, the app errors and asks you to specify one.
Accepted values are `grpc`, `http`, `http/protobuf`, and `http/json` (the last three all
mean HTTP for this tool).

For HTTP, the `/v1/traces` signal path is appended automatically if not already
present. For gRPC, the standard `TraceService/Export` method path is used.

## Custom HTTP headers

`--http-header`/`-H` (repeatable, `name=value`) adds a header to every export request.
This is how OTLP carries authentication — e.g. Honeycomb's team key:

```bash
OtelImporter traces.jsonl.zst -e https://api.honeycomb.io \
  --http-header X-Honeycomb-Team=hcik_your_api_key
```

Headers apply to both protocols: over gRPC they travel as HTTP/2 headers (gRPC
metadata), which is exactly how OTLP/gRPC carries auth. The startup banner lists header
*names* only — values are treated as secrets and never printed.

Headers can also come from the environment, in the standard comma-separated
`key1=value1,key2=value2` format:

```bash
export OTEL_EXPORTER_OTLP_HEADERS="x-honeycomb-team=hcik_your_api_key"
# or traces-specific (takes precedence over the generic one):
export OTEL_EXPORTER_OTLP_TRACES_HEADERS="x-honeycomb-team=hcik_your_api_key"
```

All sources are **merged** by header name (case-insensitive). On a conflict the
higher-precedence source wins, highest last: `OTEL_EXPORTER_OTLP_HEADERS` →
`OTEL_EXPORTER_OTLP_TRACES_HEADERS` → `--http-header`. So you can keep the API key in
the environment and still add or override individual headers on the command line.

## Filtering by time

`--from` and `--to` restrict processing to spans whose **start time** falls within an
inclusive window. Spans outside the window are dropped before anything else happens, so
they are neither exported nor counted by `--inspect`.

```bash
# Only spans started on/after 2026-05-26 01:56:00 UTC
OtelImporter traces.jsonl.zst -e <url> --from 2026-05-26T01:56:00Z

# A bounded window (either bound may be given on its own)
OtelImporter traces.jsonl.zst --inspect \
  --from 2026-05-26T01:56:00Z --to 2026-05-26T01:57:00Z
```

A value without a timezone offset is interpreted as UTC (span timestamps are UTC); a
value with an offset is honoured. A batch with no spans left after filtering is skipped
entirely — no empty request is sent upstream. Spans with no start time are kept.

## Adding attributes to spans

Every exported span automatically gets a `log.file.name` attribute set to the input
file name (e.g. `traces-1234.jsonl.zst`). Pass `--no-log-file-name` to turn that off.

Use `--attribute`/`-a` (repeatable) to add your own string attributes to every span:

```bash
OtelImporter traces-1234.jsonl.zst -e http://collector:4318 \
  --attribute octopus.prop=abc --attribute octopus.otherprop=def
```

The value is everything after the first `=`, so values may themselves contain `=` and
may be empty (`-a key=`). Attributes are appended to each span's existing attributes
(they don't replace same-named ones). Enrichment only applies when exporting — it's
ignored under `--inspect`.

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

## Design notes (These help claude keep on track)

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
