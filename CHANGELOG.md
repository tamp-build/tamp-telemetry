# Changelog

All notable changes to **Tamp.Telemetry** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/spec/v2.0.0.html).

## [0.1.2] — 2026-05-16

Covers both silent-data-loss fixes against 0.1.0 / 0.1.1. The TAM-216 fix
shipped earlier as `Tamp.Telemetry 0.1.1` via an untagged workflow_dispatch
release ; 0.1.2 is the first version with TAM-217 and is the recommended
upgrade for every adopter on 0.1.x.

### Fixed

- **TAM-217 — OTLP exporter sent traces to `/` instead of `/v1/traces`.**
  Tamp.Telemetry sets `OtlpExporterOptions.Endpoint` explicitly, which
  causes the OpenTelemetry .NET SDK to leave `AppendSignalPathToEndpoint`
  at its default `false` — the SDK treats the URL as already-per-signal
  and ships every batch to the root of the configured endpoint. Adopters
  passing `OTEL_EXPORTER_OTLP_ENDPOINT=https://otel.x.com` (the OTel-spec
  base-URL convention) had every export request go to
  `POST https://otel.x.com/` instead of `POST https://otel.x.com/v1/traces`.
  Most OTel receivers return 404 on `/`; tamp-beacon's SPA fallback
  returned 200 + HTML, silently swallowing every trace since 0.1.0.
  Force `AppendSignalPathToEndpoint = true` for HTTP exports; gRPC is
  unaffected (no path append on the gRPC wire). New
  `OtlpEndpointPathTests` spin up an in-process TCP listener and assert
  the first HTTP request line starts with `POST /v1/traces`.

- **TAM-216 — Target + Command spans were never exported.** Tamp.Core
  emits build telemetry on three `ActivitySource`s
  (`Tamp.Build`, `Tamp.Build.Targets`, `Tamp.Build.Commands`), but
  `TampTelemetry.cs` only subscribed two — and one of them was a typo
  (`Tamp.Targets` instead of `Tamp.Build.Targets`). The net effect:
  only the top-level Build span landed at the OTLP endpoint; the
  per-target and per-command spans (which carry the duration / GC /
  CPU-time signal the dashboards roll up) were silently dropped.
  Subscribed source list is now `Tamp.Build`, `Tamp.Build.Targets`,
  `Tamp.Build.Commands` — matches `Tamp.Core/Diagnostics/TampDiagnostics.cs`.
  Tripwire test now asserts the constant values match the documented
  contract so a future rename surfaces as a CI failure rather than as
  silently-missing telemetry.

## [0.1.0] — 2026-05 — initial release

### Added

OpenTelemetry emit-side bridge for Tamp builds. Wraps the
`OpenTelemetry.Exporter.OpenTelemetryProtocol` SDK setup as a one-call
adopter surface; auto-registers `Tamp.Core`'s existing ActivitySources
(`Tamp.Build`, `Tamp.Build.Targets`, `Tamp.Build.Commands`) + the
`Tamp.Build` Meter.

- **`TampTelemetry.Configure(Action<TampTelemetryOptions>)`** — fluent
  setup; returns `IDisposable` for flush-on-exit. Use when configuration
  comes from `[Parameter]` / `[Secret]` build-script state.
- **`TampTelemetry.FromEnvironment()`** — reads the canonical OpenTelemetry
  environment variables (`OTEL_EXPORTER_OTLP_ENDPOINT`,
  `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_PROTOCOL`,
  `OTEL_SERVICE_NAME`, `OTEL_SERVICE_VERSION`). The canonical CI shape —
  CI vendor supplies the env, build code stays vendor-neutral.
- **`TampTelemetryOptions`** — fluent options: `SetOtlpEndpoint`,
  `SetOtlpHeaders`, `SetProtocol` (`HttpProtobuf` / `Grpc`),
  `SetServiceName`, `SetServiceVersion`, `SetServiceInstanceId`,
  `AddResourceAttribute`.
- **Default resource attributes** — every span + metric carries
  `host.name`, `host.arch`, `os.type`, `ci.vendor`. CI vendor detection
  mirrors `Tamp.Core`'s heuristic for GitHub Actions / Azure DevOps /
  GitLab CI / Buildkite / TeamCity / CircleCI / AppVeyor / Travis /
  Jenkins / generic-CI / local.
- **No-endpoint mode** — when no OTLP endpoint is configured (typical
  local-dev case with `FromEnvironment()`), the SDK is configured WITHOUT
  an exporter. Same build code path local + CI; telemetry only emits when
  the env is wired.

### Why

Decouple Tamp.Core (build framework) from OpenTelemetry (a heavy
transitive dependency adopters opt into). Tamp.Core emits via
`ActivitySource` per ADR 0018; Tamp.Telemetry is the bridge that wires
those sources into the OTLP wire protocol. Adopters who don't want
telemetry don't take the dep; adopters who want it add a single
PackageReference.

Companion to the eventual [tamp-beacon](https://github.com/tamp-build/tamp-beacon)
self-hosted receiver, but vendor-neutral by design — points at any OTLP
endpoint (Honeycomb, Grafana, Jaeger, Datadog, tamp-beacon, etc.).

### Pinned dependencies

- OpenTelemetry 1.15.3 (matches the current safe-version line; older
  builds had CVEs in 1.10.0)
- Tamp.Core >= 1.7.0

### Tests

30 unit tests (3 TFMs × 10 cases each) covering: fluent options surface,
chaining contract, validation (empty endpoint / service name / resource
key rejected), `FromEnvironment` env-var parsing (endpoint /
service-name / case-insensitive protocol), `ActivitySource`
subscription (Tamp.Build + Tamp.Targets register; unrelated source does
not), constant-name tripwire (`Tamp.Build` / `Tamp.Targets` /
`Tamp.Build` meter), Dispose idempotency.

### Deferred to 0.2.0

- Secret-aware Activity tag redaction (`TraceProcessor` that filters tag
  values against a registered-secret list — defense in depth)
- `build_id` correlation with `Tamp.Core` 1.9.0's `--reporter=json`
  NDJSON output

### Deferred to 0.3.0

- Bundled OTLP receiver auto-installer (out of scope — telemetry
  destination is the adopter's choice; we don't ship one)
