using System.Globalization;
using LifeOS.Application.Assistant;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration.Assistant;

namespace LifeOS.Application.Nutrition.Hydration.Assistant;

// AI-008: the assistant's water intakes (kind hydration.intake.create). Preparing, reading, editing and cancelling
// a draft only touch assistant_operations; the only Hydration write is the confirmation, through the existing
// AddHydrationIntakeHandler (same validation as the Hydration screen: limits, no future day).

// The fields of a new draft; null = not stated. OccurredOn/OccurredTime: the user's explicit local date and time.
public sealed record HydrationDraftInput(int? AmountMl = null, DateOnly? OccurredOn = null, TimeOnly? OccurredTime = null);

// A change to a draft: a non-null value replaces the field, a name in Clear removes it, anything else is kept.
public sealed record HydrationDraftPatch(int? AmountMl = null, DateOnly? OccurredOn = null, TimeOnly? OccurredTime = null,
    IReadOnlyCollection<string>? Clear = null)
{
    public static readonly IReadOnlyList<string> ClearableFields =
        [HydrationDraftFields.AmountMl, HydrationDraftFields.OccurredOn, HydrationDraftFields.OccurredTime];
}

public static class HydrationOperationErrors
{
    // Water cannot be logged for a day after the user's local today.
    public const string FutureDate = "future_date";
}

// A stated quantity in millilitres: deterministic, never guessed. Only ml, cl and l are units; the result must be
// whole millilitres within the intake's limits ("0.5 l" = 500 ml; "0.3333 l" is refused, not rounded).
public static class HydrationQuantity
{
    public static int? ToMillilitres(string? amount, string? unit)
    {
        if (!AssistantInterpretationValidator.TryParseAmount(amount, out var value))
        {
            return null;
        }

        decimal? millilitres = unit switch
        {
            "ml" => value,
            "cl" => value * 10,
            "l" => value * 1000,
            _ => null
        };

        return millilitres is { } ml && ml == decimal.Truncate(ml) && ml >= HydrationIntake.MinAmountMl && ml <= HydrationIntake.MaxAmountMl
            ? (int)ml
            : null;
    }

    public static string Text(int millilitres) => millilitres.ToString(CultureInfo.InvariantCulture) + " ml";
}

// Builds drafts from structured input (ArgumentException, ParamName = the field, for a value the draft cannot hold).
public static class HydrationDraftBuilder
{
    public static HydrationIntakeDraft FromInput(HydrationDraftInput input, string? timeZoneId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Shape first (so the date is in range before it is resolved), then with the resolved occurrence.
        HydrationIntakeDraft.Create(input.AmountMl, input.OccurredOn, input.OccurredTime, occurrence: null);

        return HydrationIntakeDraft.Create(input.AmountMl, input.OccurredOn, input.OccurredTime,
            AssistantDiaryOccurrenceResolver.Resolve(input.OccurredOn, input.OccurredTime, timeZoneId, now));
    }

    // The occurrence is resolved again only when the requested date or time changes, or while it is unresolved:
    // re-sending the same values keeps it, so such a patch changes nothing.
    public static HydrationIntakeDraft Apply(HydrationIntakeDraft current, HydrationDraftPatch patch, string? timeZoneId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);
        var clear = patch.Clear ?? [];

        var amount = clear.Contains(HydrationDraftFields.AmountMl) ? null : patch.AmountMl ?? current.AmountMl;
        var date = clear.Contains(HydrationDraftFields.OccurredOn) ? null : patch.OccurredOn ?? current.RequestedDate;
        var time = clear.Contains(HydrationDraftFields.OccurredTime) ? null : patch.OccurredTime ?? current.RequestedTime;

        HydrationIntakeDraft.Create(amount, date, time, occurrence: null);

        var occurrence = date == current.RequestedDate && time == current.RequestedTime && current.Occurrence is not null
            ? current.Occurrence
            : AssistantDiaryOccurrenceResolver.Resolve(date, time, timeZoneId, now);

        return HydrationIntakeDraft.Create(amount, date, time, occurrence);
    }
}

// The Hydration rules of the shared lifecycle: a future day blocks the confirmation (shown before confirming), and
// the confirmed draft becomes one intake through AddHydrationIntakeHandler.
public sealed class HydrationDraftPolicy(AddHydrationIntakeHandler addIntake) : IAssistantDraftPolicy<HydrationIntakeDraft>
{
    public string Kind => AssistantOperationKinds.HydrationIntakeCreate;

    public int PayloadVersion => HydrationIntakeDraft.PayloadVersion;

    public IReadOnlyList<string> MissingFields(HydrationIntakeDraft draft) => draft.MissingFields;

    public IReadOnlyList<string> Issues(HydrationIntakeDraft draft, DateTimeOffset now) =>
        draft.Occurrence is { } occurrence && occurrence.LocalDate > AssistantDiaryOccurrenceResolver.Today(occurrence, now)
            ? [HydrationOperationErrors.FutureDate]
            : [];

    public bool OccursInFuture(HydrationIntakeDraft draft, DateTimeOffset now) => draft.Occurrence?.OccurredAtUtc > now;

    public async Task<AssistantExecution> ExecuteAsync(Guid userId, HydrationIntakeDraft draft, CancellationToken cancellationToken)
    {
        var occurrence = draft.Occurrence!;
        var result = await addIntake.HandleAsync(userId,
            new AddHydrationIntakeCommand(draft.AmountMl!.Value, occurrence.LocalDate, occurrence.LocalTime, occurrence.UtcOffsetMinutes),
            cancellationToken);

        return result.Status == HydrationResultStatus.Ok
            ? AssistantExecution.Created(result.Value!.Intake.Id)
            : AssistantExecution.Refused(result.Field == "date" ? HydrationOperationErrors.FutureDate : AssistantErrors.RecordRejected, result.Field);
    }
}

// The use cases of Hydration drafts over the shared lifecycle, from structured input (the HTTP API and the turn).
public sealed class HydrationAssistantOperations(AssistantOperationLifecycle<HydrationIntakeDraft> lifecycle, IUserRepository users, TimeProvider clock)
{
    // Get, confirm and cancel for the HTTP adapter; the turn's module only reads the open draft (GetActiveAsync).
    public AssistantOperationLifecycle<HydrationIntakeDraft> Lifecycle => lifecycle;

    public Task<AssistantResult<AssistantOperationView<HydrationIntakeDraft>?>> GetActiveAsync(Guid userId, Guid conversationId,
        CancellationToken cancellationToken) =>
        lifecycle.GetActiveAsync(userId, conversationId, cancellationToken);

    public async Task<AssistantResult<AssistantOperationOutcome<HydrationIntakeDraft>>> CreateAsync(Guid userId, Guid conversationId,
        HydrationDraftInput input, CancellationToken cancellationToken)
    {
        HydrationIntakeDraft draft;

        try
        {
            draft = HydrationDraftBuilder.FromInput(input, await TimeZoneAsync(userId, cancellationToken), clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return AssistantResult<AssistantOperationOutcome<HydrationIntakeDraft>>.Invalid(exception.ParamName ?? "request", exception);
        }

        return await lifecycle.CreateAsync(userId, conversationId, draft, cancellationToken);
    }

    public async Task<AssistantResult<AssistantOperationOutcome<HydrationIntakeDraft>>> UpdateAsync(Guid userId, Guid operationId, int expectedVersion,
        HydrationDraftPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var clear = patch.Clear ?? [];

        if (clear.FirstOrDefault(field => !HydrationDraftPatch.ClearableFields.Contains(field)) is { } unknown)
        {
            return AssistantResult<AssistantOperationOutcome<HydrationIntakeDraft>>.Invalid("clear", $"'{unknown}' is not a field that can be cleared.");
        }

        (string Field, bool Set)[] fields =
        [
            (HydrationDraftFields.AmountMl, patch.AmountMl is not null), (HydrationDraftFields.OccurredOn, patch.OccurredOn is not null),
            (HydrationDraftFields.OccurredTime, patch.OccurredTime is not null)
        ];

        if (fields.FirstOrDefault(field => field.Set && clear.Contains(field.Field)) is { Field: { } both })
        {
            return AssistantResult<AssistantOperationOutcome<HydrationIntakeDraft>>.Invalid("clear", $"'{both}' cannot be both set and cleared.");
        }

        var timeZoneId = await TimeZoneAsync(userId, cancellationToken);

        return await lifecycle.UpdateAsync(userId, operationId, expectedVersion,
            (draft, now) => HydrationDraftBuilder.Apply(draft, patch, timeZoneId, now), cancellationToken);
    }

    private async Task<string?> TimeZoneAsync(Guid userId, CancellationToken cancellationToken) =>
        (await users.GetByIdAsync(userId, cancellationToken))?.TimeZoneId;
}
