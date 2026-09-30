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
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    // Pinned so every machine and CI run tests against the same server version.
    public const string Image = "postgres:18.6";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection();
        services.AddInfrastructure(_container.GetConnectionString());
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.MigrateAsync();
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
