using Xunit;

namespace Tamp.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="TampTelemetryOptions"/>'s fluent surface +
/// validation. Doesn't exercise the OTLP exporter itself — those tests
/// live in the integration suite which stands up an OTLP receiver.
/// </summary>
public sealed class TampTelemetryOptionsTests
{
    [Fact]
    public void Defaults_Are_Sensible_When_Nothing_Set()
    {
        var o = new TampTelemetryOptions();
        Assert.Null(o.OtlpEndpoint);
        Assert.Null(o.OtlpHeaders);
        Assert.Null(o.ServiceName);
        Assert.Null(o.ServiceVersion);
        Assert.Null(o.ServiceInstanceId);
        Assert.Equal(OtlpProtocol.HttpProtobuf, o.Protocol);
    }

    [Fact]
    public void SetOtlpEndpoint_Stores_Value()
    {
        var o = new TampTelemetryOptions().SetOtlpEndpoint("https://otel.example.com");
        Assert.Equal("https://otel.example.com", o.OtlpEndpoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetOtlpEndpoint_Rejects_Empty(string value)
    {
        var o = new TampTelemetryOptions();
        Assert.Throws<System.ArgumentException>(() => o.SetOtlpEndpoint(value));
    }

    [Fact]
    public void SetOtlpHeaders_Stores_Auth_Header_Pair()
    {
        var o = new TampTelemetryOptions().SetOtlpHeaders("Authorization=Bearer abc123,X-Source=tamp");
        Assert.Equal("Authorization=Bearer abc123,X-Source=tamp", o.OtlpHeaders);
    }

    [Theory]
    [InlineData(OtlpProtocol.HttpProtobuf)]
    [InlineData(OtlpProtocol.Grpc)]
    public void SetProtocol_Selects_Wire_Protocol(OtlpProtocol p)
    {
        var o = new TampTelemetryOptions().SetProtocol(p);
        Assert.Equal(p, o.Protocol);
    }

    [Fact]
    public void SetServiceName_Stores_Value()
    {
        var o = new TampTelemetryOptions().SetServiceName("my-build");
        Assert.Equal("my-build", o.ServiceName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetServiceName_Rejects_Empty(string value)
    {
        var o = new TampTelemetryOptions();
        Assert.Throws<System.ArgumentException>(() => o.SetServiceName(value));
    }

    [Fact]
    public void SetServiceVersion_Stores_Value()
    {
        var o = new TampTelemetryOptions().SetServiceVersion("1.2.3-alpha.7");
        Assert.Equal("1.2.3-alpha.7", o.ServiceVersion);
    }

    [Fact]
    public void Fluent_Setters_Return_Same_Instance_For_Chaining()
    {
        var o = new TampTelemetryOptions();
        Assert.Same(o, o.SetOtlpEndpoint("https://x"));
        Assert.Same(o, o.SetOtlpHeaders("k=v"));
        Assert.Same(o, o.SetProtocol(OtlpProtocol.Grpc));
        Assert.Same(o, o.SetServiceName("svc"));
        Assert.Same(o, o.SetServiceVersion("1.0.0"));
        Assert.Same(o, o.SetServiceInstanceId("inst"));
        Assert.Same(o, o.AddResourceAttribute("custom.tag", "value"));
    }

    [Fact]
    public void AddResourceAttribute_Stores_Multiple_Attributes()
    {
        var o = new TampTelemetryOptions()
            .AddResourceAttribute("team", "platform")
            .AddResourceAttribute("env", "ci-runner-pool-2");
        Assert.Equal(2, o.ResourceAttributes.Count);
        Assert.Equal("platform", o.ResourceAttributes["team"]);
        Assert.Equal("ci-runner-pool-2", o.ResourceAttributes["env"]);
    }

    [Fact]
    public void AddResourceAttribute_Overwrites_On_Duplicate_Key()
    {
        var o = new TampTelemetryOptions()
            .AddResourceAttribute("env", "stage")
            .AddResourceAttribute("env", "prod");
        Assert.Single(o.ResourceAttributes);
        Assert.Equal("prod", o.ResourceAttributes["env"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddResourceAttribute_Rejects_Empty_Key(string key)
    {
        var o = new TampTelemetryOptions();
        Assert.Throws<System.ArgumentException>(() => o.AddResourceAttribute(key, "value"));
    }
}
