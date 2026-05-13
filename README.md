# Tamp.Telemetry

> OpenTelemetry emit-side bridge for Tamp builds. One call in `Main` and every build emits structured traces + metrics to any OTLP-speaking backend — Honeycomb, Grafana Tempo, Jaeger, Datadog (via OTLP gateway), self-hosted [tamp-beacon](https://github.com/tamp-build/tamp-beacon), or whatever OTLP receiver you point it at.

| Package | Status |
|---|---|
| `Tamp.Telemetry` | 0.1.0 (initial) |

## Why this exists

`Tamp.Core` already emits Activity spans (`Tamp.Build` for the build-level span, `Tamp.Targets` for per-target spans) + a `Tamp.Build` Meter with build-count / build-duration / peak-memory instruments — see [ADR 0018](https://github.com/tamp-build/tamp/blob/main/docs/adr/0018-diagnostics-emission-contract.md) for the emission contract. What `Tamp.Core` does NOT do is decide where those signals go. By design — telemetry destination is an adopter choice (vendor lock-in is a real cost), and bundling an OTLP exporter into Core would force every adopter to take an OpenTelemetry transitive dependency whether they want telemetry or not.

`Tamp.Telemetry` is the bridge package that wires Tamp's existing diagnostic sources into an OpenTelemetry SDK + OTLP exporter. Add a `PackageReference` to it from a build that wants telemetry; leave it off for builds that don't.

## Minimal adoption snippet

```csharp
using Tamp;
using Tamp.Telemetry;

class Build : TampBuild
{
    public static int Main(string[] args)
    {
        using var telemetry = TampTelemetry.FromEnvironment();
        //  ↑ reads OTEL_EXPORTER_OTLP_ENDPOINT + OTEL_SERVICE_NAME et al.
        //    If no endpoint env is set, the SDK is configured WITHOUT an
        //    exporter — same code path local + CI, telemetry only emits
        //    when the env is wired.

        return Execute<Build>(args);
    }

    // ... your build script ...
}
```

That's it. Set `OTEL_EXPORTER_OTLP_ENDPOINT=https://otel.example.com/v1/traces` in CI and every build sends spans + metrics. Local dev defaults to no-emit unless an adopter sets the env explicitly.

## Install

```bash
dotnet add package Tamp.Telemetry
```

Multi-targets `net8.0 / net9.0 / net10.0`. Requires `Tamp.Core >= 1.7.0`.

## Configure modes

### `TampTelemetry.FromEnvironment()` — the canonical CI shape

Reads standard OpenTelemetry environment variables:

| Env var | Effect |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Base endpoint URI (e.g. `https://otel.example.com/v1/traces`). Without this, the SDK is configured but no exporter attaches. |
| `OTEL_EXPORTER_OTLP_HEADERS` | Comma-separated `k=v` header pairs — typically auth: `Authorization=Bearer abc123` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` (default) or `grpc` |
| `OTEL_SERVICE_NAME` | `service.name` resource attribute |
| `OTEL_SERVICE_VERSION` | `service.version` resource attribute |

This is the recommended shape for CI — your CI vendor supplies the env vars (`actions/setup-otel` style secret-injection), the build code stays vendor-neutral.

### `TampTelemetry.Configure(...)` — fluent / explicit

```csharp
using var telemetry = TampTelemetry.Configure(t => t
    .SetOtlpEndpoint("https://otel.example.com/v1/traces")
    .SetOtlpHeaders($"Authorization=Bearer {ApiToken.Reveal()}")
    .SetServiceName("dasbook")
    .SetServiceVersion(Version)
    .SetProtocol(OtlpProtocol.HttpProtobuf)
    .AddResourceAttribute("team", "platform")
    .AddResourceAttribute("ci.runner.pool", Environment.GetEnvironmentVariable("RUNNER_POOL") ?? "default"));
```

Use this when configuration comes from `[Parameter]` / `[Secret]` build-script state rather than process env. Both modes are equivalent; the env-driven shape is just less typing for the common case.

## What gets emitted

### Traces (via the `Tamp.Build` + `Tamp.Targets` ActivitySources)

- **Build span** (`Tamp.Build`) — root span per `tamp <target>` invocation. Carries: `build.targets`, `build.cli_version`, `host.os`, `host.os_version`, `host.arch`, `host.cpu_count`, `host.total_memory_bytes`, `dotnet.runtime`, `ci.vendor`, `ci.is_ci`, `build.project.name`, `build.project.area`, `build.project.name.source`. Terminal tags on exit: `build.targets_skipped`, `build.targets_not_run`, `build.commands_total`, `build.exit_code`, `outcome`.
- **Target spans** (`Tamp.Targets`) — child span per target invocation. Carries: `target.name`, `target.phase`, `target.depends_on`, `target.failure_mode`, `target.is_assured_after_failure`, `target.start_working_set_bytes`, `target.attempt`, `target.actions_count`. Terminal tags on exit: `target.duration_ms`, `target.peak_memory_bytes`, `target.cpu_time_ms`, `target.allocated_bytes`, `target.gen0_collections`, `target.commands_dispatched`, `outcome`, `outcome.reason`.

### Metrics (via the `Tamp.Build` Meter)

- `tamp.builds.total` — counter, tagged with `outcome` (`success` / `failure`)
- `tamp.build.duration_ms` — histogram, tagged with `outcome`
- `tamp.build.peak_memory_bytes` — histogram, tagged with `outcome`

### Default resource attributes

Every emitted span and metric carries these — visible across all of Tamp.Telemetry's traffic:

- `service.name` / `service.version` / `service.instance.id` (from your config)
- `host.name` — `Environment.MachineName`
- `host.arch` — `x64` / `arm64` / etc.
- `os.type` — `windows` / `linux` / `darwin`
- `ci.vendor` — `github-actions` / `azure-devops` / `gitlab-ci` / `buildkite` / `teamcity` / `circleci` / `appveyor` / `travis` / `jenkins` / `ci-unknown` / `local`

## Disposal

`TampTelemetry.FromEnvironment()` and `TampTelemetry.Configure(...)` return an `IDisposable`. **Wrap in `using`** so pending spans + metrics flush before the process exits — without disposal the OTLP exporter's batched queue can drop the build's terminal events.

```csharp
using var telemetry = TampTelemetry.FromEnvironment();
return Execute<Build>(args);
//  ↑ telemetry disposes here, flushing the build.end span
```

Disposing twice is safe — the underlying `TracerProvider.Dispose` / `MeterProvider.Dispose` are idempotent.

## Backends this works with

Anything that speaks OTLP/HTTP or OTLP/gRPC. Confirmed shapes:

- **[tamp-beacon](https://github.com/tamp-build/tamp-beacon)** — the self-hosted Tamp telemetry receiver (point at `https://<beacon>/v1/traces`).
- **Honeycomb** — `OTEL_EXPORTER_OTLP_ENDPOINT=https://api.honeycomb.io` + `OTEL_EXPORTER_OTLP_HEADERS=x-honeycomb-team=<api-key>`.
- **Grafana Tempo / Mimir** — point at your Tempo OTLP endpoint.
- **Datadog** — point at your local OTLP gateway (Datadog Agent listens on OTLP since DD agent 7.34+).
- **Jaeger** — Jaeger collector accepts OTLP since 1.35+ (`https://<jaeger>:4318/v1/traces`).

## What's NOT here (yet)

- **Secret-aware Activity tag redaction**. ADR 0018's contract is that no Secret values should land in Activity tags; this satellite trusts that contract. A defense-in-depth `TraceProcessor` that scans tag values against a registered-secret list lands in 0.2.0.
- **`build_id` correlation with `--reporter=json`**. The NDJSON `build.start` event from `Tamp.Core 1.9.0`'s JsonBuildReporter has its own `build_id` GUID; the OTLP build span has its own `TraceId`. Cross-correlating them needs a small `Tamp.Core` change — 0.2.0.
- **Auto-installer for an OTLP receiver**. Out of scope: telemetry destination is the adopter's choice.

## Releasing

Releases follow the [Tamp dogfood pattern](MAINTAINERS.md): bump `<Version>` in `Directory.Build.props`, tag `v<X.Y.Z>`, GitHub Actions runs `dotnet tamp Ci` then `dotnet tamp Push`.

## License

MIT. See [LICENSE](LICENSE).
