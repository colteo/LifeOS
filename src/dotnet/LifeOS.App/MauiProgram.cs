using LifeOS.App.Services;
using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Finance;
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

		builder.Services.AddSingleton(ApiSettings.ForDevelopment());

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

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	private static HttpClient CreatePlainHttpClient(IServiceProvider services) =>
		Configure(new HttpClient(), services);

	private static HttpClient CreateAuthorizedHttpClient(IServiceProvider services) =>
		Configure(
			new HttpClient(new AuthorizationMessageHandler(services.GetRequiredService<TokenSession>(), new HttpClientHandler())),
			services);

	private static HttpClient Configure(HttpClient httpClient, IServiceProvider services)
	{
		httpClient.BaseAddress = services.GetRequiredService<ApiSettings>().BaseAddress;
		httpClient.Timeout = TimeSpan.FromSeconds(15);

		return httpClient;
	}
}
