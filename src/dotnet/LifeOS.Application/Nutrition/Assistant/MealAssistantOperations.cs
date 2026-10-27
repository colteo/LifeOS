using LifeOS.Application.Assistant;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Nutrition.Assistant;

namespace LifeOS.Application.Nutrition.Assistant;

// AI-008: the assistant's meals (kind nutrition.meal.create). Preparing, reading, editing and cancelling a draft only
// touch assistant_operations; the only meal journal write is the confirmation, through the existing CreateMealHandler.
// No nutrition estimate is requested (NUT-002 stays an explicit action of the meal journal).

// The fields of a new draft; null = not stated. OccurredOn/OccurredTime: the user's explicit local date and time.
public sealed record MealDraftInput(string? Description = null, MealType? MealType = null, DateOnly? OccurredOn = null, TimeOnly? OccurredTime = null);

// A change to a draft: a non-null value replaces the field, a name in Clear removes it, anything else is kept.
public sealed record MealDraftPatch(string? Description = null, MealType? MealType = null, DateOnly? OccurredOn = null, TimeOnly? OccurredTime = null,
    IReadOnlyCollection<string>? Clear = null)
{
    public static readonly IReadOnlyList<string> ClearableFields =
        [MealDraftFields.Description, MealDraftFields.MealType, MealDraftFields.OccurredOn, MealDraftFields.OccurredTime];
}

// Builds drafts from structured input (ArgumentException, ParamName = the field, for a value the draft cannot hold).
public static class MealDraftBuilder
{
    public static MealDraft FromInput(MealDraftInput input, string? timeZoneId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        MealDraft.Create(input.Description, input.MealType, input.OccurredOn, input.OccurredTime, occurrence: null);

        return MealDraft.Create(input.Description, input.MealType, input.OccurredOn, input.OccurredTime,
            AssistantDiaryOccurrenceResolver.Resolve(input.OccurredOn, input.OccurredTime, timeZoneId, now));
    }

    // The occurrence is resolved again only when the requested date or time changes, or while it is unresolved.
    public static MealDraft Apply(MealDraft current, MealDraftPatch patch, string? timeZoneId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);
        var clear = patch.Clear ?? [];

        var description = clear.Contains(MealDraftFields.Description) ? null : patch.Description ?? current.Description;
        var mealType = clear.Contains(MealDraftFields.MealType) ? null : patch.MealType ?? current.MealType;
        var date = clear.Contains(MealDraftFields.OccurredOn) ? null : patch.OccurredOn ?? current.RequestedDate;
        var time = clear.Contains(MealDraftFields.OccurredTime) ? null : patch.OccurredTime ?? current.RequestedTime;

        MealDraft.Create(description, mealType, date, time, occurrence: null);

        var occurrence = date == current.RequestedDate && time == current.RequestedTime && current.Occurrence is not null
            ? current.Occurrence
            : AssistantDiaryOccurrenceResolver.Resolve(date, time, timeZoneId, now);

        return MealDraft.Create(description, mealType, date, time, occurrence);
    }
}

// The meal journal's rules of the shared lifecycle: nothing blocks a complete draft (the journal accepts any day, as
// its own screen does); the confirmed draft becomes one meal through CreateMealHandler.
public sealed class MealDraftPolicy(CreateMealHandler createMeal) : IAssistantDraftPolicy<MealDraft>
{
    public string Kind => AssistantOperationKinds.NutritionMealCreate;

    public int PayloadVersion => MealDraft.PayloadVersion;

    public IReadOnlyList<string> MissingFields(MealDraft draft) => draft.MissingFields;

    public IReadOnlyList<string> Issues(MealDraft draft, DateTimeOffset now) => [];

    public bool OccursInFuture(MealDraft draft, DateTimeOffset now) => draft.Occurrence?.OccurredAtUtc > now;

    public async Task<AssistantExecution> ExecuteAsync(Guid userId, MealDraft draft, CancellationToken cancellationToken)
    {
        var occurrence = draft.Occurrence!;
        var result = await createMeal.HandleAsync(userId,
            new CreateMealCommand(draft.Description!, draft.MealType, occurrence.LocalDate, occurrence.LocalTime, occurrence.UtcOffsetMinutes),
            cancellationToken);

        return result is { Status: MealResultStatus.Ok, Meal: { } meal }
            ? AssistantExecution.Created(meal.Id)
            : AssistantExecution.Refused(AssistantErrors.RecordRejected, result.Field);
    }
}

// The use cases of meal drafts over the shared lifecycle, from structured input (the HTTP API and the turn).
public sealed class MealAssistantOperations(AssistantOperationLifecycle<MealDraft> lifecycle, IUserRepository users, TimeProvider clock)
{
    // Get, confirm and cancel for the HTTP adapter; the turn's module only reads the open draft (GetActiveAsync).
    public AssistantOperationLifecycle<MealDraft> Lifecycle => lifecycle;

    public Task<AssistantResult<AssistantOperationView<MealDraft>?>> GetActiveAsync(Guid userId, Guid conversationId,
        CancellationToken cancellationToken) =>
        lifecycle.GetActiveAsync(userId, conversationId, cancellationToken);

    public async Task<AssistantResult<AssistantOperationOutcome<MealDraft>>> CreateAsync(Guid userId, Guid conversationId, MealDraftInput input,
        CancellationToken cancellationToken)
    {
        MealDraft draft;

        try
        {
            draft = MealDraftBuilder.FromInput(input, await TimeZoneAsync(userId, cancellationToken), clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return AssistantResult<AssistantOperationOutcome<MealDraft>>.Invalid(exception.ParamName ?? "request", exception);
        }

        return await lifecycle.CreateAsync(userId, conversationId, draft, cancellationToken);
    }

    public async Task<AssistantResult<AssistantOperationOutcome<MealDraft>>> UpdateAsync(Guid userId, Guid operationId, int expectedVersion,
        MealDraftPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var clear = patch.Clear ?? [];

        if (clear.FirstOrDefault(field => !MealDraftPatch.ClearableFields.Contains(field)) is { } unknown)
        {
            return AssistantResult<AssistantOperationOutcome<MealDraft>>.Invalid("clear", $"'{unknown}' is not a field that can be cleared.");
        }

        (string Field, bool Set)[] fields =
        [
            (MealDraftFields.Description, patch.Description is not null), (MealDraftFields.MealType, patch.MealType is not null),
            (MealDraftFields.OccurredOn, patch.OccurredOn is not null), (MealDraftFields.OccurredTime, patch.OccurredTime is not null)
        ];

        if (fields.FirstOrDefault(field => field.Set && clear.Contains(field.Field)) is { Field: { } both })
        {
            return AssistantResult<AssistantOperationOutcome<MealDraft>>.Invalid("clear", $"'{both}' cannot be both set and cleared.");
        }

        var timeZoneId = await TimeZoneAsync(userId, cancellationToken);

        return await lifecycle.UpdateAsync(userId, operationId, expectedVersion, (draft, now) => MealDraftBuilder.Apply(draft, patch, timeZoneId, now),
            cancellationToken);
    }

    private async Task<string?> TimeZoneAsync(Guid userId, CancellationToken cancellationToken) =>
        (await users.GetByIdAsync(userId, cancellationToken))?.TimeZoneId;
}
