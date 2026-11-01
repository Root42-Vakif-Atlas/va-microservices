using System.Globalization;
using Elastic.CommonSchema.Serilog;
using Microsoft.AspNetCore.Builder;
using Serilog;
using Serilog.Events;

namespace VakifAtlas.ServiceDefaults.Observability;

/// <summary>Serilog registration.</summary>
public static class LoggingExtensions
{
    /// <summary>Logs ECS JSON to stdout, or plain text when <c>Logging:Format=text</c>. Never logs request bodies.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">Service name added to every log event.</param>
    public static WebApplicationBuilder AddStructuredLogging(this WebApplicationBuilder builder, string serviceName)
    {
        var useText = string.Equals(builder.Configuration["Logging:Format"], "text", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddSerilog((services, logger) =>
        {
            logger
                .ReadFrom.Configuration(builder.Configuration)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service.name", serviceName);

            if (useText)
            {
                logger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj} {correlation_id}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture);
            }
            else
            {
                logger.WriteTo.Console(new EcsTextFormatter());
            }
        });
        return builder;
    }
}
