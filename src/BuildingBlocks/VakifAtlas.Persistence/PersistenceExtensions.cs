using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using VakifAtlas.Messaging;
using VakifAtlas.ServiceDefaults;

namespace VakifAtlas.Persistence;

/// <summary>Startup database behavior bound from the <c>Database</c> configuration section.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section name these options are bound from.</summary>
    public const string Section = "Database";

    /// <summary>Applies migrations at startup in dev and test; staging and prod migrate through the Helm pre-upgrade Job.</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Runs registered seeders at startup in dev and staging; disabled in prod.</summary>
    public bool Seed { get; set; }
}

/// <summary>Idempotent seed step that runs after migrations.</summary>
public interface IDataSeeder
{
    /// <summary>Inserts seed data that is not already present.</summary>
    /// <param name="cancellationToken">Token that cancels the seed run.</param>
    Task SeedAsync(CancellationToken cancellationToken);
}

/// <summary>Registers the per-service PostgreSQL database and its startup tasks.</summary>
public static class PersistenceExtensions
{
    /// <summary>Adds the service's PostgreSQL DbContext with snake_case naming, outbox signaling, a readiness check and optional startup migration.</summary>
    /// <typeparam name="TDbContext">DbContext owned by the service.</typeparam>
    /// <param name="builder">Web application builder to configure.</param>
    /// <param name="configureDataSource">Optional hook to customize the Npgsql data source, such as mapping enums.</param>
    public static WebApplicationBuilder AddPostgres<TDbContext>(this WebApplicationBuilder builder,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null)
        where TDbContext : DbContext
    {
        var connectionString = builder.Configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        // Kerberos is not used and the distroless image lacks libgssapi, so an Npgsql GSS attempt would only log errors.
        if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
        {
            dataSourceBuilder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        }

        configureDataSource?.Invoke(dataSourceBuilder);
        NpgsqlDataSource dataSource = dataSourceBuilder.Build();
        builder.Services.AddSingleton(dataSource);

        builder.Services.AddDbContext<TDbContext>((services, options) =>
        {
            Configure(options, dataSource);
            if (services.GetService<OutboxSignalInterceptor>() is not null)
            {
                options.AddOutboxInterceptors(services);
            }
        });

        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
        builder.Services.AddHostedService<DatabaseInitializer<TDbContext>>();
        builder.Services.AddHealthChecks().AddDbContextCheck<TDbContext>("database", tags: [ServiceDefaultsExtensions.ReadyTag]);
        return builder;
    }

    /// <summary>Applies the shared Npgsql provider, migrations history table and snake_case naming.</summary>
    /// <param name="options">DbContext options to configure.</param>
    /// <param name="dataSource">Npgsql data source the context connects through.</param>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    /// <summary>Registers a scoped seeder that runs at startup when seeding is enabled.</summary>
    /// <typeparam name="TSeeder">Seeder implementation to register.</typeparam>
    /// <param name="services">Service collection to add the seeder to.</param>
    public static IServiceCollection AddDataSeeder<TSeeder>(this IServiceCollection services)
        where TSeeder : class, IDataSeeder =>
        services.AddScoped<IDataSeeder, TSeeder>();
}

/// <summary>Runs migrations and seeders in <c>StartAsync</c>, before the server accepts requests.</summary>
internal sealed class DatabaseInitializer<TDbContext>(
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<DatabaseOptions> options,
    ILogger<DatabaseInitializer<TDbContext>> logger) : IHostedService
    where TDbContext : DbContext
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.MigrateOnStartup && !options.Value.Seed)
        {
            return;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (options.Value.MigrateOnStartup)
        {
            TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            logger.DatabaseMigrated();
        }

        if (options.Value.Seed)
        {
            foreach (IDataSeeder seeder in scope.ServiceProvider.GetServices<IDataSeeder>())
            {
                await seeder.SeedAsync(cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Design-time factory for <c>dotnet ef</c> that builds the context without starting the app.</summary>
/// <typeparam name="TDbContext">DbContext the EF tools operate on.</typeparam>
public abstract class DesignTimeDbContextFactory<TDbContext> : IDesignTimeDbContextFactory<TDbContext>
    where TDbContext : DbContext
{
    /// <summary>Creates the context from <c>ConnectionStrings__Database</c> or a local fallback connection string.</summary>
    /// <param name="args">Arguments passed by the EF tools.</param>
    public TDbContext CreateDbContext(string[] args)
    {
        NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(
            Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Host=localhost;Database=design;Username=postgres;Password=postgres").Build();
        var options = new DbContextOptionsBuilder<TDbContext>();
        PersistenceExtensions.Configure(options, dataSource);
        return Create(options.Options);
    }

    /// <summary>Constructs the concrete context from the configured options.</summary>
    /// <param name="options">Options configured for PostgreSQL.</param>
    protected abstract TDbContext Create(DbContextOptions<TDbContext> options);
}

/// <summary>Cached log messages for database startup.</summary>
internal static class PersistenceLog
{
    private static readonly Action<ILogger, Exception?> _databaseMigrated = LoggerMessage.Define(
        LogLevel.Information, new EventId(1, nameof(DatabaseMigrated)), "Database migrated");

    public static void DatabaseMigrated(this ILogger logger) => _databaseMigrated(logger, null);
}
