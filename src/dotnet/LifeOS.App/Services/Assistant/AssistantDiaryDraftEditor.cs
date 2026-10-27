using System.Globalization;
using LifeOS.Contracts.Assistant;

namespace LifeOS.App.Services.Assistant;

// AI-008: the form model of a water or meal draft edit (Finance keeps AssistantDraftEditor). Same rules: it starts
// from the draft as stored and turns the user's changes into one PATCH — a changed value is sent, an emptied field is
// listed in Clear, an untouched field is left out. Only the fields of the draft's kind are ever sent (water: amount
// in ml; meal: description and meal type; both: date and time). The API validates everything again. Plain .NET,
// no MAUI.
public sealed class AssistantDiaryDraftEditor
{
	public const string AmountMlInvalidMessage = "Enter whole millilitres between 1 and 4000, e.g. 250.";
	public const string DateInvalidMessage = "Enter a valid date.";
	public const string TimeInvalidMessage = "Enter a valid time.";

	public static readonly IReadOnlyList<string> MealTypes = ["Breakfast", "Lunch", "Dinner", "Snack", "Other"];

	private readonly int? _amountMl;
	private readonly string? _description;
	private readonly string? _mealType;
	private readonly string? _occurredOn;
	private readonly string? _occurredTime;

	private AssistantDiaryDraftEditor(string kind, int? amountMl, string? description, string? mealType, string? occurredOn, string? occurredTime)
	{
		Kind = kind;
		(_amountMl, _description, _mealType, _occurredOn, _occurredTime) = (amountMl, description, mealType, occurredOn, TimeText(occurredTime));
		AmountMlText = amountMl?.ToString(CultureInfo.InvariantCulture) ?? "";
		Description = description ?? "";
		MealType = mealType ?? "";
		OccurredOn = occurredOn ?? "";
		OccurredTime = _occurredTime ?? "";
	}

	public string Kind { get; }

	public bool IsWater => Kind == AssistantOperationKindNames.HydrationIntakeCreate;

	public string AmountMlText { get; set; }

	public string Description { get; set; }

	// "" = no meal type.
	public string MealType { get; set; }

	// "yyyy-MM-dd" (input type=date); empty = today (the API's default).
	public string OccurredOn { get; set; }

	// "HH:mm" (input type=time); empty = the API's default time.
	public string OccurredTime { get; set; }

	public static AssistantDiaryDraftEditor From(AssistantOperationResponse operation) => operation switch
	{
		{ Kind: AssistantOperationKindNames.HydrationIntakeCreate, Hydration: { } water } =>
			new(operation.Kind, water.AmountMl, null, null, water.OccurredOn, water.OccurredTime),
		{ Kind: AssistantOperationKindNames.NutritionMealCreate, Meal: { } meal } =>
			new(operation.Kind, null, meal.Description, meal.MealType, meal.OccurredOn, meal.OccurredTime),
		_ => throw new ArgumentException("Not a water or meal draft.", nameof(operation))
	};

	// The PATCH for the user's changes, or (null, null) when nothing changed, or (null, error) when a value is invalid.
	public (UpdateAssistantOperationRequest? Request, string? Error) BuildPatch(int expectedVersion)
	{
		var clear = new List<string>();
		int? amountMl = null;
		string? description = null;
		string? mealType = null;
		string? occurredOn = null;
		string? occurredTime = null;

		if (IsWater)
		{
			if (string.IsNullOrWhiteSpace(AmountMlText))
			{
				if (_amountMl is not null)
				{
					clear.Add("amountMl");
				}
			}
			else if (!int.TryParse(AmountMlText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 4000)
			{
				return (null, AmountMlInvalidMessage);
			}
			else if (parsed != _amountMl)
			{
				amountMl = parsed;
			}
		}
		else
		{
			var trimmed = Description.Trim();

			if (trimmed != (_description ?? ""))
			{
				if (trimmed.Length == 0)
				{
					clear.Add("description");
				}
				else
				{
					description = trimmed;
				}
			}

			if (MealType != (_mealType ?? ""))
			{
				if (MealType.Length == 0)
				{
					clear.Add("mealType");
				}
				else
				{
					mealType = MealType;
				}
			}
		}

		if (OccurredOn != (_occurredOn ?? ""))
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

		if (time != (_occurredTime ?? ""))
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

		if (amountMl is null && description is null && mealType is null && occurredOn is null && occurredTime is null && clear.Count == 0)
		{
			return (null, null);
		}

		return (new UpdateAssistantOperationRequest(expectedVersion, null, null, null, null, null, null, occurredOn, occurredTime,
			clear.Count > 0 ? clear : null, amountMl, description, mealType), null);
	}

	// "HH:mm" from "HH:mm" or "HH:mm:ss"; null when not a time.
	private static string? TimeText(string? text) =>
		text is not null && TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
			? time.ToString("HH:mm", CultureInfo.InvariantCulture)
			: null;
}
