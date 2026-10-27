using System.Collections.Concurrent;
using LifeOS.Application.Assistant;

namespace LifeOS.UnitTests.Fakes;

// AI-007 F3: the interpreter port without any model, for tests only. It does NOT read the message: each test
// scripts the exact result it needs (an interpretation, a failure, a hang until the deadline) and the fake
// returns it, recording every request so tests can assert exactly what would have been sent. There is no
// production fake: the default production interpreter is the disabled one.
internal sealed class ScriptedAssistantInterpreter : IAssistantInterpreter
{
    private readonly ConcurrentQueue<AssistantInterpretationRequest> _requests = new();
    private readonly ConcurrentQueue<Func<AssistantInterpretationRequest, CancellationToken, Task<AssistantInterpretationResult>>> _script = new();

    public IReadOnlyList<AssistantInterpretationRequest> Requests => _requests.ToList();

    // Called before answering; lets a test interleave other work with an in-flight interpretation.
    public Func<AssistantInterpretationRequest, Task>? BeforeAnswer { get; set; }

    public ScriptedAssistantInterpreter Then(AssistantInterpretation interpretation) =>
        Then((_, _) => Task.FromResult(AssistantInterpretationResult.Success(interpretation)));

    public ScriptedAssistantInterpreter Then(AssistantInterpretationFailure failure) =>
        Then((_, _) => Task.FromResult(AssistantInterpretationResult.Failed(failure)));

    // Never answers: waits until the caller's deadline cancels it.
    public ScriptedAssistantInterpreter ThenHang() =>
        Then(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });

    public ScriptedAssistantInterpreter Then(Func<AssistantInterpretationRequest, CancellationToken, Task<AssistantInterpretationResult>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public async Task<AssistantInterpretationResult> InterpretAsync(AssistantInterpretationRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);

        if (!_script.TryDequeue(out var step))
        {
            throw new InvalidOperationException("The scripted interpreter has no answer left for this call.");
        }

        if (BeforeAnswer is { } beforeAnswer)
        {
            await beforeAnswer(request);
        }

        return await step(request, cancellationToken);
    }
}

// Builders of protocol interpretations (v2) for tests. A Stated value's span defaults to the value itself, so
// the test message must contain it (as the validator requires).
internal static class Interpretations
{
    public static AssistantTextField Stated(string value, string? span = null) => new(AssistantFieldState.Stated, value, span ?? value);

    public static AssistantTextField Inferred(string value) => new(AssistantFieldState.Inferred, value);

    public static AssistantTextField Clear { get; } = new(AssistantFieldState.Clear);

    public static AssistantDateField Date(AssistantDateKind kind, string span, int? daysAgo = null, DayOfWeek? weekday = null, int? day = null,
        int? month = null, int? year = null) =>
        new(AssistantFieldState.Stated, kind, daysAgo, weekday, day, month, year, span);

    public static AssistantTimeField Time(int hour, int minute, string span) => new(AssistantFieldState.Stated, hour, minute, span);

    public static AssistantFinanceFields Fields(
        AssistantTextField? type = null,
        AssistantTextField? amount = null,
        AssistantTextField? currency = null,
        AssistantTextField? account = null,
        AssistantTextField? category = null,
        AssistantTextField? note = null,
        AssistantDateField? date = null,
        AssistantTimeField? time = null) =>
        new(type ?? AssistantTextField.Absent, amount ?? AssistantTextField.Absent, currency ?? AssistantTextField.Absent,
            account ?? AssistantTextField.Absent, category ?? AssistantTextField.Absent, note ?? AssistantTextField.Absent,
            date ?? AssistantDateField.Absent, time ?? AssistantTimeField.Absent);

    public static AssistantHydrationFields Water(
        AssistantTextField? amount = null,
        AssistantTextField? unit = null,
        AssistantDateField? date = null,
        AssistantTimeField? time = null) =>
        new(amount ?? AssistantTextField.Absent, unit ?? AssistantTextField.Absent, date ?? AssistantDateField.Absent, time ?? AssistantTimeField.Absent);

    public static AssistantMealFields MealFields(
        AssistantTextField? description = null,
        AssistantTextField? mealType = null,
        AssistantDateField? date = null,
        AssistantTimeField? time = null) =>
        new(description ?? AssistantTextField.Absent, mealType ?? AssistantTextField.Absent, date ?? AssistantDateField.Absent,
            time ?? AssistantTimeField.Absent);

    public static AssistantInterpretation Of(AssistantIntent intent, AssistantFinanceFields? finance = null, IReadOnlyList<string>? ambiguous = null,
        AssistantUnsupportedReason? reason = null, AssistantHydrationFields? hydration = null, AssistantMealFields? meal = null) =>
        new(AssistantInterpretationProtocol.Version, intent, finance ?? AssistantFinanceFields.None, hydration ?? AssistantHydrationFields.None,
            meal ?? AssistantMealFields.None, ambiguous ?? [], reason);

    public static AssistantInterpretation Expense(AssistantFinanceFields finance) => Of(AssistantIntent.FinanceExpense, finance);

    public static AssistantInterpretation Income(AssistantFinanceFields finance) => Of(AssistantIntent.FinanceIncome, finance);

    public static AssistantInterpretation Update(AssistantFinanceFields finance, IReadOnlyList<string>? ambiguous = null) =>
        Of(AssistantIntent.DraftUpdate, finance, ambiguous);

    // AI-008: a new water intake / meal, and a change to the open draft of either.
    public static AssistantInterpretation Drink(AssistantHydrationFields hydration, IReadOnlyList<string>? ambiguous = null) =>
        Of(AssistantIntent.HydrationIntake, ambiguous: ambiguous, hydration: hydration);

    public static AssistantInterpretation Meal(AssistantMealFields meal, IReadOnlyList<string>? ambiguous = null) =>
        Of(AssistantIntent.NutritionMeal, ambiguous: ambiguous, meal: meal);

    public static AssistantInterpretation UpdateWater(AssistantHydrationFields hydration) => Of(AssistantIntent.DraftUpdate, hydration: hydration);

    public static AssistantInterpretation UpdateMeal(AssistantMealFields meal) => Of(AssistantIntent.DraftUpdate, meal: meal);
}
