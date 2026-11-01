using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog.Context;
using VakifAtlas.ServiceDefaults.Observability;

namespace VakifAtlas.ServiceDefaults.Http;

/// <summary>Reads or creates the <c>X-Correlation-Id</c> and adds it to logs, traces and the response.</summary>
/// <param name="next">The next middleware.</param>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Correlation ID header name.</summary>
    public const string Header = "X-Correlation-Id";

    /// <summary>Key for the correlation ID in <see cref="HttpContext.Items"/> and logs.</summary>
    public const string ItemKey = "correlation_id";

    /// <summary>Applies the correlation ID and calls the next middleware.</summary>
    /// <param name="context">The current request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Normalize(context.Request.Headers[Header].ToString()) ?? Guid.NewGuid().ToString();
        context.Items[ItemKey] = correlationId;
        context.Response.Headers[Header] = correlationId;

        var client = ClientContext.From(context.Request.Headers);
        Activity? activity = Activity.Current;
        activity?.SetTag("correlation.id", correlationId);
        if (client.AppVersion is not null)
        {
            activity?.SetTag("app.version", client.AppVersion);
        }

        if (client.Platform is not null)
        {
            activity?.SetTag("app.platform", client.Platform);
        }

        if (client.DeviceCompromised)
        {
            activity?.SetTag("device.integrity", "compromised");
            Telemetry.CompromisedDeviceRequests.Add(1, new KeyValuePair<string, object?>("platform", client.Platform));
        }

        using (LogContext.PushProperty(ItemKey, correlationId))
        {
            await next(context);
        }
    }

    /// <summary>Returns the value as a GUID, or <c>null</c> if invalid.</summary>
    /// <param name="value">Raw header value.</param>
    public static string? Normalize(string? value) =>
        Guid.TryParse(value, out Guid parsed) ? parsed.ToString() : null;
}

/// <summary>Forwards the correlation ID on outgoing HTTP calls.</summary>
/// <param name="accessor">Access to the current request.</param>
public sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] is string correlationId &&
            !request.Headers.Contains(CorrelationIdMiddleware.Header))
        {
            request.Headers.Add(CorrelationIdMiddleware.Header, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Client headers sent with every request.</summary>
/// <param name="AppVersion">Value of <c>X-App-Version</c>.</param>
/// <param name="Platform">Value of <c>X-Platform</c>.</param>
/// <param name="DeviceCompromised">Whether <c>X-Device-Integrity</c> is <c>compromised</c>.</param>
public sealed record ClientContext(string? AppVersion, string? Platform, bool DeviceCompromised)
{
    /// <summary>Reads the client headers, trimming long values.</summary>
    /// <param name="headers">The request headers.</param>
    public static ClientContext From(IHeaderDictionary headers) => new(
        Limit(headers["X-App-Version"].ToString(), 32),
        Limit(headers["X-Platform"].ToString(), 16),
        string.Equals(headers["X-Device-Integrity"].ToString(), "compromised", StringComparison.OrdinalIgnoreCase));

    private static string? Limit(string value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}
