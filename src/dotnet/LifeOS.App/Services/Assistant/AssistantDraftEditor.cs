using System.Globalization;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Assistant;
using LifeOS.Contracts.Finance.Categories;

namespace LifeOS.App.Services.Assistant;

// AI-007 F4: the form model of a Finance draft edit. It starts from the draft as stored and turns the user's
// changes into one PATCH: a changed value is sent, an emptied field is listed in Clear, an untouched field is
// left out (the API keeps it). Accounts and categories come only from the user's own lists (selects), never
// from typed text. The API validates everything again. Plain .NET, no MAUI.
public sealed class AssistantDraftEditor
{
	public const string ExpenseType = "Expense";
	public const string IncomeType = "Income";

	public const string AmountInvalidMessage = "Enter an amount greater than zero, e.g. 12,50.";
	public const string DateInvalidMessage = "Enter a valid date.";
	public const string TimeInvalidMessage = "Enter a valid time.";

	private readonly FinanceTransactionDraftResponse _draft;

	private AssistantDraftEditor(FinanceTransactionDraftResponse draft)
	{
		_draft = draft;
		Type = draft.Type ?? "";
		AmountText = draft.Amount is { } amount ? amount.ToString("0.####", CultureInfo.InvariantCulture).Replace('.', ',') : "";
		AccountId = draft.Account?.Id.ToString() ?? "";
		CategoryId = draft.Category?.Id.ToString() ?? "";
		Note = draft.Note ?? "";
		OccurredOn = draft.OccurredOn ?? "";
		OccurredTime = TimeText(draft.OccurredTime) ?? "";
	}

	public string Type { get; set; }

	public string AmountText { get; set; }

	public string AccountId { get; set; }

	public string CategoryId { get; set; }

	public string Note { get; set; }

	// "yyyy-MM-dd" (input type=date); empty = today (the API's default).
	public string OccurredOn { get; set; }

	// "HH:mm" (input type=time); empty = the API's default time.
	public string OccurredTime { get; set; }

	public static AssistantDraftEditor From(AssistantOperationResponse operation) =>
		new(operation.Draft ?? throw new ArgumentException("Not a Finance draft.", nameof(operation)));

	// The categories the draft may take: the user's own, of the selected type (parents and subcategories).
	public CategorySection CategoryOptions(IReadOnlyList<CategoryResponse> categories) =>
		CategoryTree.Build(categories, Type is ExpenseType or IncomeType ? Type : ExpenseType);

	// A category of the other type cannot stay selected when the type changes.
	public void ChangeType(string type, IReadOnlyList<CategoryResponse> categories)
	{
		Type = type;

		if (Guid.TryParse(CategoryId, out var id) && categories.FirstOrDefault(category => category.Id == id) is { } selected
			&& selected.Type != type)
		{
			CategoryId = "";
		}
	}

	// The PATCH for the user's changes, or (null, null) when nothing changed, or (null, error) when a value is invalid.
	public (UpdateAssistantOperationRequest? Request, string? Error) BuildPatch(int expectedVersion)
	{
		var clear = new List<string>();
		string? type = null;
		decimal? amount = null;
		Guid? accountId = null;
		Guid? categoryId = null;
		string? note = null;
		string? occurredOn = null;
		string? occurredTime = null;

		if (Type != (_draft.Type ?? ""))
		{
			if (Type.Length == 0)
			{
				clear.Add("type");
			}
			else
			{
				type = Type;
			}
		}

		if (string.IsNullOrWhiteSpace(AmountText))
		{
			if (_draft.Amount is not null)
			{
				clear.Add("amount");
			}
		}
		else if (!DecimalText.TryParse(AmountText, allowNegative: false, out var parsed) || parsed <= 0)
		{
			return (null, AmountInvalidMessage);
		}
		else if (parsed != _draft.Amount)
		{
			amount = parsed;
		}

		Select(AccountId, _draft.Account?.Id, "accountId", clear, out accountId);
		Select(CategoryId, _draft.Category?.Id, "categoryId", clear, out categoryId);

		var trimmedNote = Note.Trim();

		if (trimmedNote != (_draft.Note ?? ""))
		{
			if (trimmedNote.Length == 0)
			{
				clear.Add("note");
			}
			else
			{
				note = trimmedNote;
			}
		}

		if (OccurredOn != (_draft.OccurredOn ?? ""))
		{
			if (OccurredOn.Length == 0)
			{
				clear.Add("occurredOn");
			}
			else if (!DateOnly.TryParseExact(OccurredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			{
				return (null, DateInvalidMessage);
			}
			else
			{
				occurredOn = OccurredOn;
			}
		}

		var time = OccurredTime.Length == 0 ? "" : TimeText(OccurredTime);

		if (time is null)
		{
			return (null, TimeInvalidMessage);
		}

		if (time != (TimeText(_draft.OccurredTime) ?? ""))
		{
			if (time.Length == 0)
			{
				clear.Add("occurredTime");
			}
			else
			{
				occurredTime = time;
			}
		}

		if (type is null && amount is null && accountId is null && categoryId is null && note is null && occurredOn is null
			&& occurredTime is null && clear.Count == 0)
		{
			return (null, null);
		}

		return (new UpdateAssistantOperationRequest(expectedVersion, type, amount, null, accountId, categoryId, note, occurredOn, occurredTime,
			clear.Count > 0 ? clear : null), null);
	}

	private static void Select(string selected, Guid? current, string field, List<string> clear, out Guid? changed)
	{
		changed = null;

		if (!Guid.TryParse(selected, out var id))
		{
			if (current is not null)
			{
				clear.Add(field);
			}

			return;
		}

		if (id != current)
		{
			changed = id;
		}
	}

	// "HH:mm" from "HH:mm" or "HH:mm:ss" (seconds are dropped: the form edits minutes); null when not a time.
	private static string? TimeText(string? text) =>
		text is not null && TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
			? time.ToString("HH:mm", CultureInfo.InvariantCulture)
			: null;
}
