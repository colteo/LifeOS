using LifeOS.Contracts.WeeklyReviews;

namespace LifeOS.App.Services.WeeklyReviews;

public enum WeeklyReviewInsightsState
{
	// Reading whether insights exist (no AI call).
	Checking,

	NotGenerated,

	// Waiting for the API to generate (an AI call).
	Generating,

	Available,

	// Reading or generating failed; Retry repeats the failed step.
	Error
}

// AI-001: the state of the AI Insights section of one weekly review. Independent of the review itself:
// the deterministic review is loaded and shown by the page whatever happens here. Plain .NET, no MAUI.
public sealed class WeeklyReviewInsightsPanel(WeeklyReviewsApiClient api)
{
	private bool _failedWhileGenerating;

	public WeeklyReviewInsightsState State { get; private set; } = WeeklyReviewInsightsState.Checking;

	public WeeklyReviewInsightsResponse? Insights { get; private set; }

	public string? ErrorMessage { get; private set; }

	public bool IsBusy => State is WeeklyReviewInsightsState.Checking or WeeklyReviewInsightsState.Generating;

	public async Task LoadAsync(Guid reviewId, CancellationToken cancellationToken = default)
	{
		Start(WeeklyReviewInsightsState.Checking);
		Apply(await api.GetInsightsAsync(reviewId, cancellationToken), generating: false);
	}

	public async Task GenerateAsync(Guid reviewId, CancellationToken cancellationToken = default)
	{
		if (IsBusy)
		{
			return;
		}

		Start(WeeklyReviewInsightsState.Generating);
		Apply(await api.GenerateInsightsAsync(reviewId, cancellationToken), generating: true);
	}

	public Task RetryAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
		_failedWhileGenerating ? GenerateAsync(reviewId, cancellationToken) : LoadAsync(reviewId, cancellationToken);

	private void Start(WeeklyReviewInsightsState state)
	{
		State = state;
		Insights = null;
		ErrorMessage = null;
	}

	private void Apply(ApiResult<WeeklyReviewInsightsStateResponse> result, bool generating)
	{
		if (!result.IsSuccess || result.Value is not { } value)
		{
			State = WeeklyReviewInsightsState.Error;
			ErrorMessage = WeeklyReviewDisplay.InsightsError(result.Errors);
			_failedWhileGenerating = generating;
			return;
		}

		_failedWhileGenerating = false;

		if (value is { Status: WeeklyReviewInsightsStatuses.Available, Insights: { } insights })
		{
			State = WeeklyReviewInsightsState.Available;
			Insights = insights;
		}
		else
		{
			State = WeeklyReviewInsightsState.NotGenerated;
		}
	}
}
