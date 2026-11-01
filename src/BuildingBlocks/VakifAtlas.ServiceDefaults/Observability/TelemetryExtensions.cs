using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace VakifAtlas.ServiceDefaults.Observability;

/// <summary>Custom activity source and metrics for the application.</summary>
public static class Telemetry
{
    /// <summary>Name prefix of custom sources and meters; <c>VakifAtlas.*</c> is exported.</summary>
    public const string Prefix = "VakifAtlas";

    /// <summary>Activity source for manual spans.</summary>
    public static readonly ActivitySource Source = new($"{Prefix}.App");

    /// <summary>Meter for application metrics.</summary>
    public static readonly Meter Meter = new($"{Prefix}.App");

    /// <summary>Counts requests from rooted or jailbroken devices.</summary>
    public static readonly Counter<long> CompromisedDeviceRequests =
        Meter.CreateCounter<long>("vakifatlas.client.compromised_device_requests");

    /// <summary>Counts requests rejected by business rate limits.</summary>
    public static readonly Counter<long> UsageLimitRejections =
        Meter.CreateCounter<long>("vakifatlas.usage_limit.rejections");
}

/// <summary>OpenTelemetry registration.</summary>
public static class TelemetryExtensions
{
    /// <summary>Adds tracing and metrics, exported over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">Service name reported as <c>service.name</c>.</param>
    public static WebApplicationBuilder AddTelemetry(this WebApplicationBuilder builder, string serviceName)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        OpenTelemetryBuilder otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceNamespace: "vakif-atlas", serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName.ToLowerInvariant())]))
            .WithTracing(tracing => tracing
                .AddSource($"{Telemetry.Prefix}.*")
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal);
                    options.RecordException = true;
                })
                .AddHttpClientInstrumentation(options => options.RecordException = true)
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddMeter($"{Telemetry.Prefix}.*", "Npgsql", "Microsoft.AspNetCore.Hosting",
                    "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.RateLimiting",
                    "Microsoft.AspNetCore.Diagnostics", "System.Net.Http")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
