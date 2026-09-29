using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LifeOS.App.Services;

// Turns LifeOS API failures into readable messages for the UI. Shared by the API clients.
internal static class ApiErrors
{
	public const string UnreachableMessage = "Could not reach the LifeOS API. Check that it is running and try again.";

	public static async Task<IReadOnlyList<string>> ReadAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		try
		{
			var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(cancellationToken);

			// 400: validation problem with per-field errors.
			if (response.StatusCode == HttpStatusCode.BadRequest && problem?.Errors is { Count: > 0 } errors)
			{
				return errors.Values.SelectMany(messages => messages).ToList();
			}

			// Other failures: ProblemDetails detail, then title.
			var message = !string.IsNullOrWhiteSpace(problem?.Detail) ? problem.Detail : problem?.Title;

			if (!string.IsNullOrWhiteSpace(message))
			{
				return [message];
			}
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			// Not a JSON problem body; fall through to a generic message.
		}

		return response.StatusCode == HttpStatusCode.BadRequest
			? ["The request was rejected."]
			: [$"The LifeOS API returned an unexpected response ({(int)response.StatusCode})."];
	}

	// Network errors, timeouts and unreadable responses are reported to the UI instead of thrown.
	// Cancellation requested by the caller still propagates.
	public static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
		exception is HttpRequestException or JsonException
		|| (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

	private sealed record ProblemResponse(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}
