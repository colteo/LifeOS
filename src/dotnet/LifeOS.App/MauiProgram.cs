using LifeOS.App.Services;
using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Finance;
using LifeOS.App.Services.Gym;
using LifeOS.App.Services.Nutrition;
using LifeOS.App.Services.Onboarding;
using LifeOS.App.Services.Users;
using Microsoft.Extensions.Logging;

namespace LifeOS.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		builder.Services.AddSingleton(ApiSettings.ForCurrentBuild());
        builder.Services.AddSingleton(new PortfolioPrivacy(
            () => Preferences.Default.Get(PortfolioPrivacy.PreferenceKey, false),
            hidden => Preferences.Default.Set(PortfolioPrivacy.PreferenceKey, hidden)));

		// Authentication: the session endpoints use a plain HttpClient (no token, no refresh).
		builder.Services.AddSingleton<RefreshTokenStore>();
		builder.Services.AddSingleton(services => new AuthApiClient(CreatePlainHttpClient(services)));
		builder.Services.AddSingleton<TokenSession>();
		builder.Services.AddSingleton<AuthService>();

		// LifeOS API clients send the access token and refresh it once on 401.
		builder.Services.AddSingleton(services => new MeApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new OnboardingApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new AccountsApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new CategoriesApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new TransactionsApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new AnalyticsApiClient(CreateAuthorizedHttpClient(services)));
        builder.Services.AddSingleton(services => new BudgetsApiClient(CreateAuthorizedHttpClient(services)));
        builder.Services.AddSingleton(services => new RecurringApiClient(CreateAuthorizedHttpClient(services)));
        builder.Services.AddSingleton(services => new PlannedExpensesApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new WorkoutProgramsApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new ExercisesApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new WorkoutSessionsApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new ActiveProgramApiClient(CreateAuthorizedHttpClient(services)));
		builder.Services.AddSingleton(services => new NutritionApiClient(
			CreateAuthorizedHttpClient(services), CreateAuthorizedHttpClient(services, ApiTimeouts.NutritionAi)));
		builder.Services.AddSingleton<RestSkips>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	private static HttpClient CreatePlainHttpClient(IServiceProvider services) =>
		Configure(new HttpClient(), services);

	private static HttpClient CreateAuthorizedHttpClient(IServiceProvider services, TimeSpan? timeout = null) =>
		Configure(
			new HttpClient(new AuthorizationMessageHandler(services.GetRequiredService<TokenSession>(), new HttpClientHandler())),
			services,
			timeout);

	// ApiTimeouts.Default for every LifeOS API call; only the AI-backed Nutrition calls get a longer one.
	private static HttpClient Configure(HttpClient httpClient, IServiceProvider services, TimeSpan? timeout = null)
	{
		httpClient.BaseAddress = services.GetRequiredService<ApiSettings>().BaseAddress;
		httpClient.Timeout = timeout ?? ApiTimeouts.Default;

		return httpClient;
	}
}
