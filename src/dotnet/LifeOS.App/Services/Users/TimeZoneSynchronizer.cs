namespace LifeOS.App.Services.Users;

// AUTO-001: the server's time zone follows the device. Sends the device's IANA zone to
// PUT /api/me/time-zone only when it differs from the last zone the server acknowledged for this
// user. Best effort: it never throws and never touches the session or the UI; a failure is simply
// retried on the next sign-in, app start or resume. Delegates allow testing without MAUI.
public sealed class TimeZoneSynchronizer
{
	public const string PreferenceKey = "lifeos.time-zone.acknowledged";

	private readonly Func<string?> _readDeviceZone;
	private readonly Func<string?> _loadAcknowledged;
	private readonly Action<string> _saveAcknowledged;
	private readonly Func<string, CancellationToken, Task<bool>> _sendAsync;
	private int _running;

	public TimeZoneSynchronizer(
		Func<string?> readDeviceZone,
		Func<string?> loadAcknowledged,
		Action<string> saveAcknowledged,
		Func<string, CancellationToken, Task<bool>> sendAsync)
	{
		_readDeviceZone = readDeviceZone;
		_loadAcknowledged = loadAcknowledged;
		_saveAcknowledged = saveAcknowledged;
		_sendAsync = sendAsync;
	}

	public async Task SynchronizeAsync(Guid userId, CancellationToken cancellationToken = default)
	{
		// One attempt at a time: start-up and resume can trigger together; a skipped trigger is
		// covered by the attempt in flight or by the next one.
		if (Interlocked.Exchange(ref _running, 1) == 1)
		{
			return;
		}

		try
		{
			var zone = _readDeviceZone()?.Trim();

			if (string.IsNullOrEmpty(zone))
			{
				return;
			}

			var acknowledgement = Acknowledgement(userId, zone);

			if (_loadAcknowledged() == acknowledgement)
			{
				return;
			}

			// Only a server acknowledgement is remembered, so any failure is retried later.
			if (await _sendAsync(zone, cancellationToken))
			{
				_saveAcknowledged(acknowledgement);
			}
		}
		catch (Exception)
		{
			// Best effort: nothing to report; the next trigger tries again.
		}
		finally
		{
			Volatile.Write(ref _running, 0);
		}
	}

	// Per user: another account signing in on this device must still send its zone.
	public static string Acknowledgement(Guid userId, string timeZoneId) => $"{userId:D} {timeZoneId}";
}
