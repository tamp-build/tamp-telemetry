using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Tamp.Telemetry;

/// <summary>
/// Adopter entry point for routing Tamp's build diagnostics (the
/// <c>Tamp.Build</c>, <c>Tamp.Build.Targets</c>, <c>Tamp.Build.Commands</c>
/// ActivitySources + the <c>Tamp.Build</c> Meter defined in
/// <c>Tamp.Core</c>) through OpenTelemetry to any OTLP-speaking backend
/// — Honeycomb, Grafana Tempo / Mimir, Jaeger, self-hosted tamp-beacon,
/// etc.
/// </summary>
/// <remarks>
/// <para>
/// Tamp.Core emits Activity spans + Meter instruments via the conventions
/// documented in ADR 0018. The protocol contract is already in place; this
/// satellite is the glue that wires those sources into an OpenTelemetry
/// <see cref="TracerProvider"/> + <see cref="MeterProvider"/> + OTLP exporter
/// so adopters get telemetry export with one call from <c>Main</c>:
/// </para>
/// <code>
/// using var telemetry = TampTelemetry.Configure(t => t
///     .SetOtlpEndpoint("https://otel.example.com/v1/traces")
///     .SetServiceName("dasbook")
///     .SetServiceVersion(Version));
///
/// return Build.Execute&lt;Build&gt;(args);
/// </code>
/// <para>
/// Or, the canonical CI shape that reads standard OpenTelemetry environment
/// variables (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, <c>OTEL_SERVICE_NAME</c>,
/// <c>OTEL_EXPORTER_OTLP_HEADERS</c>, etc.):
/// </para>
/// <code>
/// using var telemetry = TampTelemetry.FromEnvironment();
/// return Build.Execute&lt;Build&gt;(args);
/// </code>
/// <para>
/// The returned handle is <see cref="IDisposable"/> — disposing flushes any
/// pending spans/metrics and shuts down the providers. Wrap in <c>using</c>
/// so Main's exit path is guaranteed to flush.
/// </para>
/// </remarks>
public sealed class TampTelemetry : IDisposable
{
    /// <summary>The three ActivitySources Tamp.Core defines (ADR 0018).</summary>
    public const string BuildActivitySource = "Tamp.Build";
    public const string TargetsActivitySource = "Tamp.Build.Targets";
    public const string CommandsActivitySource = "Tamp.Build.Commands";

    /// <summary>The Meter Tamp.Core defines for build-level counters/histograms.</summary>
    public const string BuildMeter = "Tamp.Build";

    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;

    private TampTelemetry(TracerProvider tracerProvider, MeterProvider meterProvider)
    {
        _tracerProvider = tracerProvider;
        _meterProvider = meterProvider;
    }

    /// <summary>
    /// Construct a configured <see cref="TampTelemetry"/> from a fluent
    /// callback. Returns an <see cref="IDisposable"/> handle — call
    /// <see cref="Dispose"/> (or <c>using</c>) on Main's exit path so pending
    /// spans/metrics flush before the process terminates.
    /// </summary>
    public static TampTelemetry Configure(Action<TampTelemetryOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var options = new TampTelemetryOptions();
        configure(options);
        return Build(options);
    }

    /// <summary>
    /// Construct a <see cref="TampTelemetry"/> from the canonical
    /// OpenTelemetry environment variables — the standard CI shape.
    /// </summary>
    /// <remarks>
    /// <para>Read in priority order from these envs:</para>
    /// <list type="bullet">
    ///   <item><b>OTEL_EXPORTER_OTLP_ENDPOINT</b> — base endpoint URI</item>
    ///   <item><b>OTEL_EXPORTER_OTLP_HEADERS</b> — comma-separated <c>k=v</c> auth pairs</item>
    ///   <item><b>OTEL_EXPORTER_OTLP_PROTOCOL</b> — <c>http/protobuf</c> (default) or <c>grpc</c></item>
    ///   <item><b>OTEL_SERVICE_NAME</b> — service.name resource attribute</item>
    ///   <item><b>OTEL_SERVICE_VERSION</b> — service.version resource attribute</item>
    /// </list>
    /// <para>
    /// If no endpoint is set, this returns a disposable but configures the
    /// OpenTelemetry SDK without any exporter — useful for "telemetry on
    /// when OTEL_* present, no-op otherwise" CI shapes that want the same
    /// build script local and in CI.
    /// </para>
    /// </remarks>
    public static TampTelemetry FromEnvironment()
    {
        var options = new TampTelemetryOptions();

        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrEmpty(endpoint)) options.SetOtlpEndpoint(endpoint);

        var headers = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        if (!string.IsNullOrEmpty(headers)) options.SetOtlpHeaders(headers);

        var protocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        if (string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase))
            options.SetProtocol(OtlpProtocol.Grpc);

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        if (!string.IsNullOrEmpty(serviceName)) options.SetServiceName(serviceName);

        var serviceVersion = Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION");
        if (!string.IsNullOrEmpty(serviceVersion)) options.SetServiceVersion(serviceVersion);

        return Build(options);
    }

    private static TampTelemetry Build(TampTelemetryOptions options)
    {
        var resource = BuildResource(options);

        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(BuildActivitySource, TargetsActivitySource, CommandsActivitySource);

        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(BuildMeter);

        if (!string.IsNullOrEmpty(options.OtlpEndpoint))
        {
            tracerBuilder.AddOtlpExporter(o => ConfigureOtlp(o, options, "/v1/traces"));
            meterBuilder.AddOtlpExporter(o => ConfigureOtlp(o, options, "/v1/metrics"));
        }

        return new TampTelemetry(tracerBuilder.Build()!, meterBuilder.Build()!);
    }

    private static ResourceBuilder BuildResource(TampTelemetryOptions options)
    {
        var resource = ResourceBuilder.CreateDefault();

        if (!string.IsNullOrEmpty(options.ServiceName))
            resource.AddService(
                serviceName: options.ServiceName,
                serviceVersion: options.ServiceVersion,
                serviceInstanceId: options.ServiceInstanceId);

        // Default attributes that adopters almost always want — overridable via
        // additional resource builder calls in the future. Host info comes from
        // Tamp.Core's existing HostProfile snapshot but the values we surface
        // here are intentionally minimal: anything sensitive (CI tokens, repo
        // URLs with PATs, etc.) stays off the resource by design.
        resource.AddAttributes(new KeyValuePair<string, object>[]
        {
            new("host.name", Environment.MachineName),
            new("host.arch", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()),
            new("os.type", DetectOsType()),
            new("ci.vendor", DetectCiVendor()),
        });

        foreach (var (k, v) in options.ResourceAttributes) resource.AddAttributes(new[] { new KeyValuePair<string, object>(k, v) });

        return resource;
    }

    private static void ConfigureOtlp(OpenTelemetry.Exporter.OtlpExporterOptions o, TampTelemetryOptions options, string signalPath)
    {
        var url = options.OtlpEndpoint!;
        if (options.Protocol != OtlpProtocol.Grpc)
        {
            // OpenTelemetry .NET treats an explicitly-set OtlpExporterOptions.
            // Endpoint as the full per-signal URL — AppendSignalPathToEndpoint
            // defaults to false in that path AND is `internal` in the 1.15
            // line (so we can't toggle it). The canonical OTel-spec behavior
            // for OTEL_EXPORTER_OTLP_ENDPOINT (which is what TampTelemetry
            // adopters typically pass) is to APPEND the signal-specific path
            // /v1/traces or /v1/metrics. Do that here so traces don't ship
            // to the bare root of the endpoint — which silently 200s through
            // a SPA fallback like tamp-beacon's and drops every span.
            //
            // Only append when the URL doesn't already end with the signal
            // path — adopters who pass the full per-signal URL via
            // SetOtlpEndpoint should not get /v1/traces/v1/traces.
            //
            // gRPC dials the endpoint as-is (no HTTP path); doesn't apply.
            if (!url.EndsWith(signalPath, StringComparison.OrdinalIgnoreCase))
                url = url.TrimEnd('/') + signalPath;
        }
        o.Endpoint = new Uri(url);
        if (!string.IsNullOrEmpty(options.OtlpHeaders)) o.Headers = options.OtlpHeaders;
        o.Protocol = options.Protocol == OtlpProtocol.Grpc
            ? OpenTelemetry.Exporter.OtlpExportProtocol.Grpc
            : OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
    }

    private static string DetectOsType()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "darwin";
        return "other";
    }

    private static string DetectCiVendor()
    {
        // Mirror Tamp.Core's CiHost.Detect heuristic without taking a hard
        // dependency on that internal API — we read the same env vars.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return "github-actions";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"))) return "azure-devops";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITLAB_CI"))) return "gitlab-ci";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUILDKITE"))) return "buildkite";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAMCITY_VERSION"))) return "teamcity";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CIRCLECI"))) return "circleci";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPVEYOR"))) return "appveyor";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRAVIS"))) return "travis";
        if (Environment.GetEnvironmentVariable("JENKINS_URL") is not null) return "jenkins";
        if (Environment.GetEnvironmentVariable("CI") == "true") return "ci-unknown";
        return "local";
    }

    public void Dispose()
    {
        _tracerProvider.Dispose();
        _meterProvider.Dispose();
    }
}
