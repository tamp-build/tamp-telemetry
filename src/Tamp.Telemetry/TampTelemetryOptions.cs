namespace Tamp.Telemetry;

/// <summary>OTLP wire protocol. <see cref="HttpProtobuf"/> is the default (broader proxy support).</summary>
public enum OtlpProtocol
{
    HttpProtobuf,
    Grpc,
}

/// <summary>
/// Fluent options for <see cref="TampTelemetry.Configure"/>. Exposed as a
/// mutable bag rather than a record so the fluent setters are discoverable
/// in IDE intellisense and the call-site reads like the rest of the Tamp
/// satellite catalogue.
/// </summary>
public sealed class TampTelemetryOptions
{
    /// <summary>
    /// Base OTLP endpoint URI (e.g. <c>https://otel.example.com/v1/traces</c>).
    /// Leave null to skip the OTLP exporter entirely — useful for adopters
    /// who want the same build script local and in CI but only emit telemetry
    /// when an endpoint is configured via environment.
    /// </summary>
    public string? OtlpEndpoint { get; private set; }

    /// <summary>Comma-separated <c>k=v</c> header pairs (typically auth: <c>Authorization=Bearer ...</c>).</summary>
    public string? OtlpHeaders { get; private set; }

    public OtlpProtocol Protocol { get; private set; } = OtlpProtocol.HttpProtobuf;

    public string? ServiceName { get; private set; }
    public string? ServiceVersion { get; private set; }
    public string? ServiceInstanceId { get; private set; }

    internal Dictionary<string, object> ResourceAttributes { get; } = new();

    public TampTelemetryOptions SetOtlpEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint must be non-empty.", nameof(endpoint));
        OtlpEndpoint = endpoint;
        return this;
    }

    public TampTelemetryOptions SetOtlpHeaders(string headers)
    {
        OtlpHeaders = headers;
        return this;
    }

    public TampTelemetryOptions SetProtocol(OtlpProtocol protocol)
    {
        Protocol = protocol;
        return this;
    }

    public TampTelemetryOptions SetServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name must be non-empty.", nameof(serviceName));
        ServiceName = serviceName;
        return this;
    }

    public TampTelemetryOptions SetServiceVersion(string serviceVersion)
    {
        ServiceVersion = serviceVersion;
        return this;
    }

    public TampTelemetryOptions SetServiceInstanceId(string serviceInstanceId)
    {
        ServiceInstanceId = serviceInstanceId;
        return this;
    }

    /// <summary>
    /// Add a custom resource attribute. Repeat to add multiple. Values
    /// surface on every emitted span and metric as resource attributes.
    /// </summary>
    public TampTelemetryOptions AddResourceAttribute(string key, object value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Attribute key must be non-empty.", nameof(key));
        ResourceAttributes[key] = value;
        return this;
    }
}
