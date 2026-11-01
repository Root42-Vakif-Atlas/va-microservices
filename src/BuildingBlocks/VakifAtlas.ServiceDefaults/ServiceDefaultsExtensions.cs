using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;
using Serilog;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.ServiceDefaults.Observability;
using VakifAtlas.ServiceDefaults.Versioning;

namespace VakifAtlas.ServiceDefaults;

/// <summary>Shared service setup: logging, telemetry, health checks, errors, JSON and API versioning.</summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Tag for health checks included in <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Registers the services every VakifAtlas service needs.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">Service name used in logs and traces.</param>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("Http:MaxRequestBodyBytes") ?? 1_048_576;
        });

        builder.AddStructuredLogging(serviceName);
        builder.AddTelemetry(serviceName);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<CorrelationIdHandler>());

        builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
        builder.Services.AddApiProblemDetails();
        builder.Services.AddVakifAtlasApiVersioning();
        builder.Services.AddHealthChecks();

        var trustedNetworks = builder.Configuration.GetSection("Http:ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            if (trustedNetworks.Length == 0)
            {
                // Only the gateway can reach the service, and it sets the headers itself.
                return;
            }

            // Strip CDN/WAF and ingress hops so rate limiting sees the real client IP.
            // Example: Http__ForwardedHeaders__KnownNetworks__0=10.0.0.0/8
            foreach (var network in trustedNetworks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }

            options.ForwardLimit = null;
        });

        return builder;
    }

    /// <summary>Applies the contract's JSON rules: camelCase names and string enums.</summary>
    /// <param name="options">Serializer options to configure.</param>
    public static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }

    /// <summary>Adds the shared middleware; call before authentication and endpoints.</summary>
    /// <param name="app">The application.</param>
    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
                : context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal) ? Serilog.Events.LogEventLevel.Verbose
                : Serilog.Events.LogEventLevel.Information;
        });
        return app;
    }

    /// <summary>Maps <c>/health/live</c>, <c>/health/ready</c> and, outside production, OpenAPI.</summary>
    /// <param name="app">The application.</param>
    /// <param name="configureScalar">Optional customization of the Scalar API reference, such as extra documents.</param>
    public static WebApplication MapDefaultEndpoints(this WebApplication app, Action<ScalarOptions>? configureScalar = null)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous();

        if (!app.Environment.IsProduction())
        {
            app.MapOpenApi().WithDocumentPerVersion().AllowAnonymous();
            if (configureScalar is null)
            {
                app.MapScalarApiReference().AllowAnonymous();
            }
            else
            {
                app.MapScalarApiReference(configureScalar).AllowAnonymous();
            }
        }

        return app;
    }
}
