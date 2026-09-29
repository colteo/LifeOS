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
		builder.Services.AddSingleton(services => new AccountsApiClient(new HttpClient
		{
			BaseAddress = services.GetRequiredService<ApiSettings>().BaseAddress,
			Timeout = TimeSpan.FromSeconds(15)
		}));

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
