namespace LifeOS.App.Services.Notifications;

public enum NotificationTargetKind
{
	Test,
	WeeklyReview,
	FinanceRecurring,
	FinancePlannedExpense
}

public sealed record NotificationTarget(NotificationTargetKind Kind, Guid? Id);

// AUTO-001 §11: a tapped notification carries only `type` and an opaque `id`. They choose where to go
// inside the authenticated app; they never authorize anything (the API checks ownership on load).
// Unknown or malformed data is ignored: the app simply opens.
public static class NotificationTap
{
	public static NotificationTarget? Parse(string? type, string? id) => type switch
	{
		"test" => new NotificationTarget(NotificationTargetKind.Test, null),
		"weekly_review" when Guid.TryParse(id, out var reviewId) && reviewId != Guid.Empty =>
			new NotificationTarget(NotificationTargetKind.WeeklyReview, reviewId),
		"finance_recurring" => new NotificationTarget(NotificationTargetKind.FinanceRecurring, ParseId(id)),
		"finance_planned_expense" => new NotificationTarget(NotificationTargetKind.FinancePlannedExpense, ParseId(id)),
		_ => null
	};

	// AUTO-003A: both Finance reminders open the Planned tab of Transactions, where the current month's
	// due recurring occurrences and one-off planned expenses are confirmed inline. The id (rule or
	// planned expense) is not needed to get there.
	public const string FinancePlannedPath = "finance/transactions?tab=planned";

	// The page to open, or null to open LifeOS where it is. The test notification needs no page; a
	// weekly review opens its saved review (AUTO-002), whose page loads it through the API.
	public static string? PathFor(NotificationTarget target) => target switch
	{
		{ Kind: NotificationTargetKind.WeeklyReview, Id: { } reviewId } => $"weekly-reviews/{reviewId:D}",
		{ Kind: NotificationTargetKind.FinanceRecurring or NotificationTargetKind.FinancePlannedExpense } => FinancePlannedPath,
		_ => null
	};

	private static Guid? ParseId(string? id) => Guid.TryParse(id, out var value) && value != Guid.Empty ? value : null;
}

// The latest tapped notification, waiting until the user is signed in to be opened once.
public sealed class PendingNotificationNavigation
{
	private readonly Lock _lock = new();
	private NotificationTarget? _pending;

	public event Action? Changed;

	public void Set(NotificationTarget target)
	{
		lock (_lock)
		{
			_pending = target;
		}

		Changed?.Invoke();
	}

	// At most once per tap.
	public NotificationTarget? TryTake()
	{
		lock (_lock)
		{
			var pending = _pending;
			_pending = null;

			return pending;
		}
	}
}

// FCM token rotation (Android FirebaseMessagingService.OnNewToken) → the App re-registers if signed in.
public static class PushTokenEvents
{
	public static event Action? TokenChanged;

	public static void RaiseTokenChanged() => TokenChanged?.Invoke();
}
