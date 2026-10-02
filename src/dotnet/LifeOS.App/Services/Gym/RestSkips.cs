namespace LifeOS.App.Services.Gym;

// Rests the user ended early. Client-only: skipping changes no data, so it is remembered for the
// app process (across page navigation), not stored. A rest is identified by its session and start.
public sealed class RestSkips
{
	private readonly Lock _lock = new();
	private readonly HashSet<(Guid SessionId, DateTimeOffset StartedAtUtc)> _skipped = [];

	public void Skip(Guid sessionId, DateTimeOffset restStartedAtUtc)
	{
		lock (_lock)
		{
			_skipped.Add((sessionId, restStartedAtUtc));
		}
	}

	public bool IsSkipped(Guid sessionId, DateTimeOffset restStartedAtUtc)
	{
		lock (_lock)
		{
			return _skipped.Contains((sessionId, restStartedAtUtc));
		}
	}
}
