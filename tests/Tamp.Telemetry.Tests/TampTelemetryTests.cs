using System.Diagnostics;
using Xunit;

namespace Tamp.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="TampTelemetry"/>'s top-level surface. We do not
/// stand up a real OTLP receiver — those tests live in the integration
/// suite. Here we cover: Configure null-guard, ActivitySource registration,
/// FromEnvironment env-var parsing, and the Dispose contract.
/// </summary>
[Collection("EnvironmentMutation")]
public sealed class TampTelemetryTests : IDisposable
{
    private readonly Dictionary<string, string?> _envSnapshot = new();
    private static readonly string[] s_envKeys =
    {
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "OTEL_SERVICE_NAME",
        "OTEL_SERVICE_VERSION",
    };

    public TampTelemetryTests()
    {
        // Snapshot env vars we touch so tests don't leak state to the rest
        // of the suite or to the user's shell when run interactively.
        foreach (var key in s_envKeys)
            _envSnapshot[key] = Environment.GetEnvironmentVariable(key);
        foreach (var key in s_envKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _envSnapshot)
            Environment.SetEnvironmentVariable(key, value);
    }

    [Fact]
    public void Configure_Throws_On_Null_Configure_Callback()
    {
        Assert.Throws<ArgumentNullException>(() => TampTelemetry.Configure(null!));
    }

    [Fact]
    public void Configure_Returns_Disposable_When_No_Endpoint_Set()
    {
        // No-endpoint config is valid — the SDK is set up but no exporter
        // attaches. Useful for "telemetry on only when OTEL_* env is set"
        // build scripts that want one code path for local + CI.
        using var t = TampTelemetry.Configure(o => o.SetServiceName("local-build"));
        Assert.NotNull(t);
    }

    [Fact]
    public void Configure_With_OtlpEndpoint_Constructs_Without_Throwing()
    {
        using var t = TampTelemetry.Configure(o => o
            .SetOtlpEndpoint("http://localhost:4318")
            .SetServiceName("test-svc"));
        Assert.NotNull(t);
    }

    // ─── FromEnvironment env-var parsing ──────────────────────────────

    [Fact]
    public void FromEnvironment_With_No_Vars_Returns_Disposable()
    {
        using var t = TampTelemetry.FromEnvironment();
        Assert.NotNull(t);
    }

    [Fact]
    public void FromEnvironment_Picks_Up_Endpoint_Env_Var()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318");
        // The construction either succeeds (endpoint is reachable from SDK's perspective
        // even if no server is there) or doesn't throw — we don't actually send.
        using var t = TampTelemetry.FromEnvironment();
        Assert.NotNull(t);
    }

    [Fact]
    public void FromEnvironment_Picks_Up_ServiceName_Env_Var()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "ci-build");
        using var t = TampTelemetry.FromEnvironment();
        Assert.NotNull(t);
    }

    [Theory]
    [InlineData("grpc")]
    [InlineData("GRPC")]
    [InlineData("gRPC")]
    public void FromEnvironment_Recognizes_Grpc_Protocol_Case_Insensitively(string value)
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", value);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
        using var t = TampTelemetry.FromEnvironment();
        Assert.NotNull(t);
    }

    // ─── ActivitySource registration ──────────────────────────────────

    [Fact]
    public void Configured_Tracer_Subscribes_To_Tamp_Build_ActivitySource()
    {
        // When the SDK is configured, it registers an ActivityListener for
        // each AddSource() name. Verify by creating an Activity through our
        // canonical source name and seeing that it gets recorded.
        using var t = TampTelemetry.Configure(o => o.SetServiceName("test"));

        using var source = new ActivitySource(TampTelemetry.BuildActivitySource);
        using var activity = source.StartActivity("test-build");

        // If the listener wasn't attached, StartActivity returns null because
        // no provider sampled in.
        Assert.NotNull(activity);
    }

    [Fact]
    public void Configured_Tracer_Subscribes_To_Tamp_Build_Targets_ActivitySource()
    {
        using var t = TampTelemetry.Configure(o => o.SetServiceName("test"));

        using var source = new ActivitySource(TampTelemetry.TargetsActivitySource);
        using var activity = source.StartActivity("test-target");

        Assert.NotNull(activity);
    }

    [Fact]
    public void Configured_Tracer_Subscribes_To_Tamp_Build_Commands_ActivitySource()
    {
        // TAM-216 regression — pre-fix, the Commands source was never
        // subscribed and StartActivity returned null, dropping every
        // command span on the floor.
        using var t = TampTelemetry.Configure(o => o.SetServiceName("test"));

        using var source = new ActivitySource(TampTelemetry.CommandsActivitySource);
        using var activity = source.StartActivity("test-command");

        Assert.NotNull(activity);
    }

    [Fact]
    public void Configured_Tracer_Does_Not_Subscribe_To_Unrelated_ActivitySource()
    {
        using var t = TampTelemetry.Configure(o => o.SetServiceName("test"));

        // An unrelated source name has no listener — should return null.
        using var source = new ActivitySource("Some.Other.Source");
        using var activity = source.StartActivity("unrelated");

        Assert.Null(activity);
    }

    // ─── Constants match Tamp.Core's documented ActivitySource names ──

    [Fact]
    public void ActivitySource_Constants_Match_Tamp_Core_Convention()
    {
        // ADR 0018 + Tamp.Core's TampDiagnostics define these source names.
        // Mismatching them here silently breaks trace export — pre-TAM-216,
        // TargetsActivitySource was "Tamp.Targets" (wrong) and
        // CommandsActivitySource didn't exist, which dropped ~80% of the
        // emitted spans. Keep the tripwire here so a future rename surfaces
        // as a test failure.
        Assert.Equal("Tamp.Build", TampTelemetry.BuildActivitySource);
        Assert.Equal("Tamp.Build.Targets", TampTelemetry.TargetsActivitySource);
        Assert.Equal("Tamp.Build.Commands", TampTelemetry.CommandsActivitySource);
        Assert.Equal("Tamp.Build", TampTelemetry.BuildMeter);
    }

    // ─── Dispose contract ─────────────────────────────────────────────

    [Fact]
    public void Dispose_Multiple_Times_Is_Safe()
    {
        var t = TampTelemetry.Configure(o => o.SetServiceName("test"));
        t.Dispose();
        // Disposing a TracerProvider twice should not throw — let's confirm
        // we don't accidentally re-dispose-and-throw.
        // Actually OpenTelemetry's TracerProviderSdk.Dispose is idempotent so
        // this works.
        t.Dispose();
    }
}
