using System.Security.Cryptography;
using LifeOS.Api.Authentication;
using LifeOS.Application.Authentication;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Users;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.Http;

// Hosts the real API pipeline (routing, JWT validation, authorization) in memory.
// User, session and Finance persistence use in-memory repositories; PostgreSQL is never contacted.
internal sealed class LifeOSApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly bool _developmentSignInEnabled;
    private readonly string _signingKey;

    public LifeOSApiFactory(
        string environment = "Development",
        bool developmentSignInEnabled = true,
        string? signingKey = null)
    {
        _environment = environment;
        _developmentSignInEnabled = developmentSignInEnabled;
        _signingKey = signingKey ?? NewSigningKey();
        Accounts = new InMemoryAccountRepository(OpeningBalances);
    }

    public InMemoryUserRepository Users { get; } = new();

    public InMemoryUserSessionRepository Sessions { get; } = new();

    public InMemoryAccountRepository Accounts { get; }

    public InMemoryOpeningBalanceRepository OpeningBalances { get; } = new();

    public InMemoryCategoryRepository Categories { get; } = new();

    public InMemoryTransactionRepository Transactions { get; } = new();

    // The host's TimeProvider: real time plus an adjustable offset (e.g. to expire authorization codes).
    public AdjustableTimeProvider Clock { get; } = new();

    public static string NewSigningKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public LifeOSTokenOptions TokenOptions => Services.GetRequiredService<LifeOSTokenOptions>();

    // A valid access token for any user id, issued by the host's own issuer.
    public string IssueAccessToken(Guid userId) => Services.GetRequiredService<AccessTokenIssuer>().Issue(userId);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);

        // Explicit values, so local User Secrets cannot change the outcome of a test.
        builder.UseSetting("ConnectionStrings:PostgreSQL", "Host=unused.invalid;Database=unused");
        builder.UseSetting("Authentication:LifeOS:SigningKey", _signingKey);
        builder.UseSetting(DevelopmentSignIn.EnabledKey, _developmentSignInEnabled ? "true" : "false");

        // Dummy Google client: registers the Google handler without contacting Google.
        builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
        builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IUserRepository>(Users);
            services.AddSingleton<IUserSessionRepository>(Sessions);
            services.AddSingleton<IAccountRepository>(Accounts);
            services.AddSingleton<IOpeningBalanceRepository>(OpeningBalances);
            services.AddSingleton<ICategoryRepository>(Categories);
            services.AddSingleton<ITransactionRepository>(Transactions);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

internal sealed class AdjustableTimeProvider : TimeProvider
{
    private TimeSpan _offset;

    public void Advance(TimeSpan by) => _offset += by;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;
}
