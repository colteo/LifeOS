using System.Net.Http.Json;
using LifeOS.Contracts.Notifications;

namespace LifeOS.App.Services.Notifications;

// AUTO-003A: GET/PUT /api/notification-preferences through the authenticated pipeline (the API takes
// the owner from the access token). Failures carry the API's readable message.
public sealed class NotificationPreferencesApiClient
{
	public const string Path = "api/notification-preferences";

	private readonly HttpClient _httpClient;

	public NotificationPreferencesApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public async Task<ApiResult<NotificationPreferencesResponse>> GetAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(Path, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<NotificationPreferencesResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var value = await response.Content.ReadFromJsonAsync<NotificationPreferencesResponse>(cancellationToken);

			return value is null
				? ApiResult<NotificationPreferencesResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<NotificationPreferencesResponse>.Success(value);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<NotificationPreferencesResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public async Task<ApiResult<bool>> SaveAsync(SetNotificationPreferencesRequest preferences, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PutAsJsonAsync(Path, preferences, cancellationToken);

			return response.IsSuccessStatusCode
				? ApiResult<bool>.Success(true)
				: ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}

// The Settings form for reminders and quiet hours. Times are the "HH:mm" values of <input type="time">
// (a browser may add seconds; they are dropped). Checked here only so the user sees the problem before
// saving; the API validates again.
public sealed class NotificationPreferencesForm
{
	public bool RecurringTransactionReminders { get; set; } = true;

	public bool PlannedExpenseReminders { get; set; } = true;

	public string QuietHoursStart { get; set; } = "22:00";

	public string QuietHoursEnd { get; set; } = "08:00";

	public static NotificationPreferencesForm From(NotificationPreferencesResponse response) => new()
	{
		RecurringTransactionReminders = response.RecurringTransactionReminders,
		PlannedExpenseReminders = response.PlannedExpenseReminders,
		QuietHoursStart = response.QuietHoursStart,
		QuietHoursEnd = response.QuietHoursEnd
	};

	// Null when valid.
	public string? Validate()
	{
		if (NormalizeTime(QuietHoursStart) is not { } start || NormalizeTime(QuietHoursEnd) is not { } end)
		{
			return "Enter a start and an end time for quiet hours.";
		}

		return start == end ? "Quiet hours must start and end at different times." : null;
	}

	// Call only after Validate() returned null.
	public SetNotificationPreferencesRequest ToRequest() => new(
		RecurringTransactionReminders,
		PlannedExpenseReminders,
		NormalizeTime(QuietHoursStart),
		NormalizeTime(QuietHoursEnd));

	// "HH:mm" or "HH:mm:ss" → "HH:mm"; anything else → null.
	public static string? NormalizeTime(string? value) =>
		TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.None, out var time)
			? time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
			: null;
}
