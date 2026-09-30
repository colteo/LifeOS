using LifeOS.Api.Authentication;
using LifeOS.Api.Finance;
using LifeOS.Api.Onboarding;
using LifeOS.Api.Users;
using LifeOS.Application.Authentication.RefreshSession;
using LifeOS.Application.Authentication.RevokeSession;
using LifeOS.Application.Authentication.StartSession;
using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Application.Finance.Accounts.SetOpeningBalance;
using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Finance.Transactions.GetRecentTransactions;
using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Application.Onboarding.CompleteOnboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Application.Users.SignInWithExternalIdentity;
using LifeOS.Infrastructure;
using LifeOS.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration
    .GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException(
        "Connection string 'PostgreSQL' not found.");

builder.Services.AddInfrastructure(connectionString);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CreateAccountHandler>();
builder.Services.AddScoped<GetAccountsHandler>();
builder.Services.AddScoped<SetOpeningBalanceHandler>();
builder.Services.AddScoped<GetAccountBalancesHandler>();
builder.Services.AddScoped<CreateCategoryHandler>();
builder.Services.AddScoped<GetCategoriesHandler>();
builder.Services.AddScoped<CreateTransactionHandler>();
builder.Services.AddScoped<GetTransactionsHandler>();
builder.Services.AddScoped<GetRecentTransactionsHandler>();

builder.Services.AddLifeOSAuthentication(builder.Configuration);
builder.Services.AddScoped<SignInWithExternalIdentityHandler>();
builder.Services.AddScoped<StartSessionHandler>();
builder.Services.AddScoped<RefreshSessionHandler>();
builder.Services.AddScoped<RevokeSessionHandler>();
builder.Services.AddScoped<GetCurrentUserHandler>();
builder.Services.AddScoped<SetUpFinanceProfileHandler>();
builder.Services.AddScoped<CompleteOnboardingHandler>();

var developmentSignInEnabled = DevelopmentSignIn.IsEnabled(builder);
var googleSignInEnabled = GoogleSignIn.IsEnabled(builder.Configuration);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi()
        .AllowAnonymous();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapGet("/health/database", async (LifeOSDbContext dbContext) =>
{
    var canConnect = await dbContext.Database.CanConnectAsync();

    return canConnect
        ? Results.Ok(new { database = "connected" })
        : Results.Problem("Database connection failed.");
})
.AllowAnonymous();

app.MapAccountEndpoints();
app.MapCategoryEndpoints();
app.MapTransactionEndpoints();
app.MapAuthEndpoints(developmentSignInEnabled);

if (googleSignInEnabled)
{
    app.MapGoogleSignInEndpoints();
}
app.MapMeEndpoints();
app.MapOnboardingEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

// Exposes the entry point to WebApplicationFactory in LifeOS.IntegrationTests.
public partial class Program;
