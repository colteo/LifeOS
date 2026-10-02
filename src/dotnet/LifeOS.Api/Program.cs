using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Api.Authentication;
using LifeOS.Api.Finance;
using LifeOS.Api.Gym;
using LifeOS.Api.Onboarding;
using LifeOS.Api.Users;
using LifeOS.Application.Authentication.RefreshSession;
using LifeOS.Application.Authentication.RevokeSession;
using LifeOS.Application.Authentication.StartSession;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Application.Finance.Accounts.SetOpeningBalance;
using LifeOS.Application.Finance.Accounts.UpdateAccount;
using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.DeleteCategory;
using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Application.Finance.Categories.RenameCategory;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Finance.Transactions.DeleteTransaction;
using LifeOS.Application.Finance.Transactions.GetRecentTransactions;
using LifeOS.Application.Finance.Transactions.GetTransaction;
using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Application.Finance.Transactions.UpdateTransaction;
using LifeOS.Application.Gym.Exercises.CreateExercise;
using LifeOS.Application.Gym.Exercises.GetExercises;
using LifeOS.Application.Gym.Programs.Blocks;
using LifeOS.Application.Gym.Programs.CreateWorkoutProgram;
using LifeOS.Application.Gym.Programs.DeleteWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutPrograms;
using LifeOS.Application.Gym.Programs.RenameWorkoutProgram;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Gym.Training;
using LifeOS.Application.Onboarding.CompleteOnboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Application.Users.SignInWithExternalIdentity;
using LifeOS.Infrastructure;
using LifeOS.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;

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
builder.Services.AddScoped<UpdateAccountHandler>();
builder.Services.AddScoped<DeleteAccountHandler>();
builder.Services.AddScoped<GetAccountBalancesHandler>();
builder.Services.AddScoped<ReconcileAccountHandler>();
builder.Services.AddScoped<CreateCategoryHandler>();
builder.Services.AddScoped<GetCategoriesHandler>();
builder.Services.AddScoped<RenameCategoryHandler>();
builder.Services.AddScoped<DeleteCategoryHandler>();
builder.Services.AddScoped<CreateTransactionHandler>();
builder.Services.AddScoped<GetTransactionsHandler>();
builder.Services.AddScoped<GetRecentTransactionsHandler>();
builder.Services.AddScoped<GetTransactionHandler>();
builder.Services.AddScoped<UpdateTransactionHandler>();
builder.Services.AddScoped<DeleteTransactionHandler>();
builder.Services.AddScoped<GetMonthlyAnalyticsHandler>();
builder.Services.AddScoped<GetMonthlyBudgetHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Recurring.RecurringHandler>();
builder.Services.AddScoped<SetMonthlyBudgetHandler>();
builder.Services.AddScoped<DeleteMonthlyBudgetHandler>();
builder.Services.AddScoped<CreateExerciseHandler>();
builder.Services.AddScoped<GetExercisesHandler>();
builder.Services.AddScoped<GetWorkoutProgramsHandler>();
builder.Services.AddScoped<GetWorkoutProgramHandler>();
builder.Services.AddScoped<CreateWorkoutProgramHandler>();
builder.Services.AddScoped<RenameWorkoutProgramHandler>();
builder.Services.AddScoped<DeleteWorkoutProgramHandler>();
builder.Services.AddScoped<AddWorkoutHandler>();
builder.Services.AddScoped<RenameWorkoutHandler>();
builder.Services.AddScoped<DeleteWorkoutHandler>();
builder.Services.AddScoped<ReorderWorkoutsHandler>();
builder.Services.AddScoped<AddWorkoutBlockHandler>();
builder.Services.AddScoped<UpdateWorkoutBlockHandler>();
builder.Services.AddScoped<DeleteWorkoutBlockHandler>();
builder.Services.AddScoped<ReorderWorkoutBlocksHandler>();
builder.Services.AddScoped<StartWorkoutSessionHandler>();
builder.Services.AddScoped<GetWorkoutSessionHandler>();
builder.Services.AddScoped<RecordWorkoutSetHandler>();
builder.Services.AddScoped<FinishWorkoutSessionHandler>();
builder.Services.AddScoped<DiscardWorkoutSessionHandler>();
builder.Services.AddScoped<GetTrainingProgramsHandler>();

builder.Services.AddLifeOSAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddScoped<SignInWithExternalIdentityHandler>();
builder.Services.AddScoped<StartSessionHandler>();
builder.Services.AddScoped<RefreshSessionHandler>();
builder.Services.AddScoped<RevokeSessionHandler>();
builder.Services.AddScoped<GetCurrentUserHandler>();
builder.Services.AddScoped<SetUpFinanceProfileHandler>();
builder.Services.AddScoped<CompleteOnboardingHandler>();

var developmentSignInEnabled = DevelopmentSignIn.IsEnabled(builder);
var googleSignInEnabled = GoogleSignIn.IsEnabled(builder.Configuration, builder.Environment);

// Outside Development the API runs behind Render's edge / load-balancer proxy, which terminates TLS
// and forwards plain HTTP with X-Forwarded-Proto / X-Forwarded-For. Without these headers the API
// would see http://, so Google sign-in would build http://…/signin-google and cookies would not be
// Secure. Render's proxy addresses are not published, so no proxy address is pinned (the
// loopback-only defaults would ignore every proxied request). This is safe only because the service
// is reachable solely through Render's public edge proxy (Render Free web services cannot receive
// private-network traffic), which writes the real values; ForwardLimit = 1 uses only the last
// (proxy-written) entry, never one supplied by the client. Host is not taken from headers.
// If the service moves away from Render Free, or private networking is ever used, review this trust
// configuration. Development has no proxy and ignores these headers.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

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

// First, so everything after it (HTTPS redirection, authentication including the Google
// /signin-google callback, redirects) sees the original scheme and client address.
if (!app.Environment.IsDevelopment())
{
    app.UseForwardedHeaders();
}

app.UseHttpsRedirection();

// Explicit, so authentication runs after the forwarded headers (when implicit, ASP.NET Core adds it
// at the start of the pipeline, before them).
app.UseAuthentication();
app.UseAuthorization();

// Development-only connectivity check for local setup (docs/development/local-development.md).
// Not mapped elsewhere: anonymous polling would keep the database awake.
if (app.Environment.IsDevelopment())
{
    app.MapGet("/health/database", async (LifeOSDbContext dbContext) =>
    {
        var canConnect = await dbContext.Database.CanConnectAsync();

        return canConnect
            ? Results.Ok(new { database = "connected" })
            : Results.Problem("Database connection failed.");
    })
    .AllowAnonymous();
}

app.MapAccountEndpoints();
app.MapAccountReconciliationEndpoints();
app.MapCategoryEndpoints();
app.MapTransactionEndpoints();
app.MapAnalyticsEndpoints();
app.MapBudgetEndpoints();
app.MapRecurringEndpoints();
app.MapExerciseEndpoints();
app.MapWorkoutProgramEndpoints();
app.MapWorkoutSessionEndpoints();
app.MapTrainingEndpoints();
app.MapAuthEndpoints(developmentSignInEnabled);

if (googleSignInEnabled)
{
    app.MapGoogleSignInEndpoints();
}
app.MapMeEndpoints();
app.MapOnboardingEndpoints();

app.Run();

// Exposes the entry point to WebApplicationFactory in LifeOS.IntegrationTests.
public partial class Program;
