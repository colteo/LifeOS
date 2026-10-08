using LifeOS.Infrastructure;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace LifeOS.IntegrationTests.PostgreSql;

// One disposable PostgreSQL container for all PostgreSQL-backed tests.
//
// The schema is created ONLY by applying the real EF Core migration chain (MigrateAsync, never
// EnsureCreated): ux_categories_user_sibling_name is hand-written migration SQL and is not part of
// the EF model, so only the migrations produce the real schema.
//
// Repositories are the real Infrastructure implementations, resolved through AddInfrastructure and
// the Application ports. Each CreateScope() is one simulated request with its own DbContext.
//
// Requires Docker. Without it, InitializeAsync fails and every PostgreSQL test fails with the
// Testcontainers error; there is no in-memory fallback.
//
// The server image is pinned (DefaultImage) so every machine and CI run tests against the same server
// version. AI-004: the image must provide the pgvector extension (the AddJournalMemory migration runs
// CREATE EXTENSION vector), so it is the pgvector project's image of pgvector 0.8.6 on PostgreSQL 18.6
// (the same server version as the previous postgres:18.6 pin). LIFEOS_POSTGRES_IMAGE overrides it for a
// compatibility run of the same suite on another major, e.g.
// LIFEOS_POSTGRES_IMAGE=pgvector/pgvector:0.8.6-pg17-trixie (LifeOS needs PostgreSQL 15+: NULLS NOT
// DISTINCT; plain postgres images no longer work because they lack pgvector).
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string DefaultImage = "pgvector/pgvector:0.8.6-pg18-trixie";
    public const string ImageVariable = "LIFEOS_POSTGRES_IMAGE";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    private ServiceProvider? _services;

    // The image in use: DefaultImage, or LIFEOS_POSTGRES_IMAGE when set.
    public static string Image { get; } = ResolveImage(Environment.GetEnvironmentVariable(ImageVariable));

    // The started server's version (server_version), e.g. "17.6 (Debian 17.6-1.pgdg13+1)".
    public string ServerVersion { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection();
        services.AddInfrastructure(_container.GetConnectionString());
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using var scope = CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;
        await database.MigrateAsync();

        ServerVersion = await database
            .SqlQueryRaw<string>("SELECT current_setting('server_version') AS \"Value\"")
            .SingleAsync();
    }

    // Absent: the pinned default. Set: a plain image reference (no whitespace); a blank value is a
    // mistake rather than a request for the default.
    public static string ResolveImage(string? value)
    {
        if (value is null)
        {
            return DefaultImage;
        }

        var image = value.Trim();

        if (image.Length == 0 || image.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"{ImageVariable} must be a Docker image reference such as postgres:17 (got '{value}').");
        }

        return image;
    }

    public AsyncServiceScope CreateScope() =>
        (_services ?? throw new InvalidOperationException("The PostgreSQL fixture is not initialized.")).CreateAsyncScope();

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

// All PostgreSQL test classes share the container and run one after another; each test's own
// concurrent operations still use separate scopes.
[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}
