using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Tamp.Telemetry.Tests;

/// <summary>
/// TAM-217 regression — asserts the OTLP exporter sends to
/// <c>/v1/traces</c> (and not bare <c>/</c>) when an adopter passes
/// the base endpoint URL via the canonical
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> shape. Spins up a one-shot TCP
/// listener, emits a Tamp.Build activity through Tamp.Telemetry, and
/// reads the first HTTP request line off the wire.
/// </summary>
[Collection("EnvironmentMutation")]
public sealed class OtlpEndpointPathTests
{
    [Fact]
    public async Task Http_Exporter_Appends_V1_Traces_To_Base_Endpoint()
    {
        // Bind to an OS-assigned free port to avoid races on hard-coded ports.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Accept one connection async; the exporter will connect from
        // inside the Activity StartActivity / Dispose path.
        string firstLine = "";
        var capture = Task.Run(() =>
        {
            using var client = listener.AcceptTcpClient();
            client.ReceiveTimeout = 2000;
            using var stream = client.GetStream();
            var buf = new byte[1024];
            var n = stream.Read(buf, 0, buf.Length);
            var text = Encoding.ASCII.GetString(buf, 0, n);
            // First HTTP request line, e.g. "POST /v1/traces HTTP/1.1"
            firstLine = text.Split('\n')[0].Trim();
        });

        using (var t = TampTelemetry.Configure(o => o
            .SetOtlpEndpoint($"http://127.0.0.1:{port}")
            .SetServiceName("tam-217-test")))
        {
            using var source = new ActivitySource(TampTelemetry.BuildActivitySource);
            using var activity = source.StartActivity("test-span");
            Assert.NotNull(activity);
            // Dispose telemetry below (closing the using block) flushes the
            // BatchExportProcessor — at that point the exporter writes to
            // the listener.
        }

        // Give the capture task a bounded window to complete.
        await capture.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("/v1/traces", firstLine);
        Assert.StartsWith("POST /v1/traces", firstLine);
    }

    [Fact]
    public async Task Http_Exporter_Does_Not_Double_Append_When_Endpoint_Already_Ends_With_Signal_Path()
    {
        // Adopter passes the full signal-path URL (some receivers ship docs
        // that way). AppendSignalPathToEndpoint=true is documented as "only
        // append when not already present" — verify Tamp.Telemetry doesn't
        // produce /v1/traces/v1/traces in that case.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string firstLine = "";
        var capture = Task.Run(() =>
        {
            using var client = listener.AcceptTcpClient();
            client.ReceiveTimeout = 2000;
            using var stream = client.GetStream();
            var buf = new byte[1024];
            var n = stream.Read(buf, 0, buf.Length);
            firstLine = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();
        });

        using (var t = TampTelemetry.Configure(o => o
            .SetOtlpEndpoint($"http://127.0.0.1:{port}/v1/traces")
            .SetServiceName("tam-217-test-double")))
        {
            using var source = new ActivitySource(TampTelemetry.BuildActivitySource);
            using var activity = source.StartActivity("test-span");
            Assert.NotNull(activity);
        }

        await capture.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith("POST /v1/traces", firstLine);
        Assert.DoesNotContain("/v1/traces/v1/traces", firstLine);
    }
}
