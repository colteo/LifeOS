using LifeOS.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeOS.Infrastructure.Notifications.Fcm;

internal static class FcmRegistration
{
    // Registers the one FCM sender. The credential is loaded now, so an invalid key fails startup
    // instead of the first send. Google types stay inside this namespace.
    public static IServiceCollection AddFcmPushNotifications(this IServiceCollection services, FcmOptions fcm)
    {
        var credential = FcmCredentials.Load(fcm.ServiceAccountJson);
        var httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = FcmPushNotificationSender.BaseAddress,
            Timeout = FcmPushNotificationSender.Timeout
        };

        return services.AddSingleton<IPushNotificationSender>(provider => new FcmPushNotificationSender(
            httpClient,
            credential,
            fcm.ProjectId,
            provider.GetService<ILogger<FcmPushNotificationSender>>() ?? NullLogger<FcmPushNotificationSender>.Instance));
    }
}
