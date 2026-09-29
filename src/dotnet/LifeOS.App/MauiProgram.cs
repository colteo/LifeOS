using LifeOS.App.Services;
using LifeOS.App.Services.Finance;
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
		builder.Services.AddSingleton(services => new AccountsApiClient(CreateApiHttpClient(services)));
		builder.Services.AddSingleton(services => new CategoriesApiClient(CreateApiHttpClient(services)));
		builder.Services.AddSingleton(services => new TransactionsApiClient(CreateApiHttpClient(services)));

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	private static HttpClient CreateApiHttpClient(IServiceProvider services) => new()
	{
		BaseAddress = services.GetRequiredService<ApiSettings>().BaseAddress,
		Timeout = TimeSpan.FromSeconds(15)
	};
}
