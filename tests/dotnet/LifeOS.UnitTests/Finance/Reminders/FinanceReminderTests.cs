using LifeOS.Application.Automation;
using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Application.Finance.Reminders;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Notifications;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Reminders;

// AUTO-003A: both Finance reminder handlers through the real tick engine (claim, fenced completion,
// outbox, Phase A) with in-memory stores. Discovery SQL and Finance due rules on PostgreSQL are in
// FinanceReminderPersistenceTests.
public class FinanceReminderTests
{
    private const string Rome = "Europe/Rome";
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-0000000000a3");
    private static readonly DateOnly Date = new(2026, 10, 15);

    // 2026-10-15 09:00 CEST = 07:00Z.
    private static readonly DateTimeOffset Due = new(2026, 10, 15, 7, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Due.AddMinutes(5));
    private readonly InMemoryAutomationExecutionStore _executions = new();
    private readonly InMemoryDeviceRegistrationRepository _devices = new();
    private readonly InMemoryNotificationDeliveryStore _deliveries;
    private readonly InMemoryNotificationPreferencesRepository _preferences = new();
    private readonly InMemoryFinanceReminderRepository _reminders;
    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly InMemoryRecurringRepository _recurring;
    private readonly InMemoryPlannedExpenses _planned = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePushNotificationSender _sender = new();
    private readonly Account _account;
    private readonly Category _category;

    public FinanceReminderTests()
    {
        _deliveries = new InMemoryNotificationDeliveryStore(_devices);
        _reminders = new InMemoryFinanceReminderRepository(_executions);
        _recurring = new InMemoryRecurringRepository(new InMemoryAccountRepository(), new InMemoryCategoryRepository(), _transactions);
        _reminders.TimeZoneIds.Add(Rome);
        _preferences.TimeZones[UserA] = Rome;
        _account = Account.Create(UserA, "Bank", AccountType.BankAccount, "EUR", Due.AddDays(-300));
        _category = Category.Create(UserA, "Rent", CategoryType.Expense, parent: null, Due.AddDays(-300));
        _devices.UpsertAsync(DeviceRegistration.Register(UserA, "phone-installation-0001", DevicePlatform.Android, "token-a", true, Due.AddDays(-1)), default).GetAwaiter().GetResult();
    }

    // ---- Recurring ----

    [Fact]
    public async Task Recurring_DueOccurrence_IsRemindedOnce_WithSafeCopyAndItsOccurrenceKey()
    {
        var rule = AddRule(dayOfMonth: 15);

        await TickAsync();
        await TickAsync();

        var execution = Assert.Single(_executions.Rows);
        Assert.Equal((RecurringTransactionReminderHandler.Type, $"{rule.Id:D}:2026-10", Rome, Due, Due.AddHours(24), AutomationExecutionStatus.Succeeded),
            (execution.AutomationType, execution.OccurrenceKey, execution.TimeZoneId, execution.ScheduledForUtc, execution.ExpiresAtUtc, execution.Status));

        var delivery = Assert.Single(_deliveries.Rows);
        Assert.Equal((NotificationType.RecurringTransactionReminder, "finance_recurring", (Guid?)rule.Id, NotificationDeliveryStatus.Sent),
            (delivery.Type, delivery.ResourceType, delivery.ResourceId, delivery.Status));

        var (_, message) = Assert.Single(_sender.Sent);
        Assert.Equal(("LifeOS", "A recurring transaction needs your confirmation"), (message.Title, message.Body));
        Assert.Equal(new Dictionary<string, string> { ["type"] = "finance_recurring", ["id"] = rule.Id.ToString("D") }, message.Data);
        AssertNoFinanceDetail(message, "Rent", "900", "EUR", "Bank");
    }

    [Theory]
    [InlineData(OccurrenceStatus.Confirmed)]
    [InlineData(OccurrenceStatus.Skipped)]
    public async Task Recurring_AlreadyProcessed_IsNotReminded(OccurrenceStatus status)
    {
        var rule = AddRule(dayOfMonth: 15);
        var transaction = Transaction.CreateExpense(UserA, _account.Id, _category.Id, 900m, "EUR", Due, null, Due);
        _recurring.States.Add(RecurringOccurrenceState.Create(rule, 2026, 10, status, status == OccurrenceStatus.Confirmed ? transaction.Id : null, Due));

        await TickAsync();

        Assert.Equal(AutomationExecutionStatus.Succeeded, Assert.Single(_executions.Rows).Status);
        Assert.Empty(_deliveries.Rows);
    }

    [Fact]
    public async Task Recurring_NoLongerDue_BecauseTheRuleMovedLater_IsNotReminded()
    {
        var rule = AddRule(dayOfMonth: 15);
        rule.Update(rule.Name, rule.AccountId, rule.CategoryId, rule.Amount, 20, null, Due);

        await TickAsync();

        Assert.Empty(_deliveries.Rows);
    }

    [Fact]
    public async Task Recurring_Disabled_IsNotReminded()
    {
        AddRule(dayOfMonth: 15);
        _preferences.Set(UserA, recurring: false);

        await TickAsync();

        Assert.Empty(_deliveries.Rows);
        Assert.Equal(AutomationExecutionStatus.Succeeded, Assert.Single(_executions.Rows).Status);
    }

    [Fact]
    public async Task Recurring_InsideQuietHours_IsDelayedUntilTheirEnd()
    {
        AddRule(dayOfMonth: 15);
        _preferences.Set(UserA, start: new TimeOnly(8, 0), end: new TimeOnly(10, 0));

        await TickAsync();
        await TickAsync();

        Assert.Empty(_sender.Sent);
        var delivery = Assert.Single(_deliveries.Rows);
        Assert.Equal((NotificationDeliveryStatus.Pending, (DateTimeOffset?)Due.AddHours(1)), (delivery.Status, delivery.NextAttemptAtUtc));

        _clock.UtcNow = Due.AddHours(1);
        await TickAsync();

        Assert.Single(_sender.Sent);
        Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
    }

    [Fact]
    public async Task Recurring_FailedAttempt_IsRetried_WithOneLogicalNotification()
    {
        var rule = AddRule(dayOfMonth: 15);
        var flaky = new FlakyRecurringRepository(_recurring, failures: 1);

        await TickAsync(Recurring(flaky));

        var execution = Assert.Single(_executions.Rows);
        Assert.Equal((AutomationExecutionStatus.FailedRetryable, "Unhandled"), (execution.Status, execution.LastFailureCode));
        Assert.Empty(_deliveries.Rows);

        _clock.Advance(TimeSpan.FromMinutes(10));
        await TickAsync(Recurring(flaky));
        await TickAsync(Recurring(flaky));

        Assert.Equal((AutomationExecutionStatus.Succeeded, 2), (Assert.Single(_executions.Rows).Status, execution.AttemptCount));
        Assert.Equal(rule.Id, Assert.Single(_deliveries.Rows).ResourceId);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task Recurring_DoesNotMutateFinance()
    {
        var rule = AddRule(dayOfMonth: 15);

        await TickAsync();

        Assert.Equal([rule], _recurring.Rules);
        Assert.Empty(_recurring.States);
        Assert.Empty(_transactions.Transactions);
    }

    [Fact]
    public async Task Recurring_FindDue_IsNothingBeforeTheLocalReminderTime()
    {
        AddRule(dayOfMonth: 15);

        Assert.Empty(await Recurring().FindDueAsync(Due.AddMinutes(-1), 25, default));
    }

    [Fact]
    public async Task Recurring_InvalidOccurrenceKey_IsAPermanentFailure()
    {
        var result = await Recurring().ExecuteAsync(Occurrence(RecurringTransactionReminderHandler.Type, "not-a-key"), default);

        Assert.Equal("InvalidOccurrenceKey", Assert.IsType<AutomationResult.Permanent>(result).Code);
    }

    [Fact]
    public void Recurring_OccurrenceKey_RoundTrips()
    {
        var ruleId = Guid.CreateVersion7();
        var key = RecurringTransactionReminderHandler.OccurrenceKey(ruleId, 2026, 2);

        Assert.Equal($"{ruleId:D}:2026-02", key);
        Assert.True(RecurringTransactionReminderHandler.TryParseOccurrenceKey(key, out var parsed, out var year, out var month));
        Assert.Equal((ruleId, 2026, 2), (parsed, year, month));
        Assert.True(AutomationExecution.IsStableCode(key, AutomationExecution.MaxOccurrenceKeyLength));
    }

    // ---- Planned expenses ----

    [Fact]
    public async Task Planned_DueExpense_IsRemindedOnce_WithSafeCopyAndItsOccurrenceKey()
    {
        var expense = AddPlanned(Date);

        await TickAsync();
        await TickAsync();

        var execution = Assert.Single(_executions.Rows);
        Assert.Equal((PlannedExpenseReminderHandler.Type, $"{expense.Id:D}:2026-10-15", AutomationExecutionStatus.Succeeded),
            (execution.AutomationType, execution.OccurrenceKey, execution.Status));

        var delivery = Assert.Single(_deliveries.Rows);
        Assert.Equal((NotificationType.PlannedExpenseReminder, "finance_planned_expense", (Guid?)expense.Id),
            (delivery.Type, delivery.ResourceType, delivery.ResourceId));

        var (_, message) = Assert.Single(_sender.Sent);
        Assert.Equal(("LifeOS", "A planned expense is due"), (message.Title, message.Body));
        AssertNoFinanceDetail(message, "Dentist", "250", "EUR", "Bank", "Rent");
    }

    [Fact]
    public async Task Planned_CancelledOrConfirmed_IsNotReminded()
    {
        var cancelled = AddPlanned(Date);
        var confirmed = AddPlanned(Date);
        _planned.States.Add(PlannedExpenseState.Cancel(cancelled, Date, Due));
        _planned.States.Add(PlannedExpenseState.Confirm(confirmed, Date,
            Transaction.CreateExpense(UserA, _account.Id, _category.Id, 250m, "EUR", Due, null, Due), Due));

        await TickAsync();

        Assert.Equal(2, _executions.Rows.Count);
        Assert.Empty(_deliveries.Rows);
    }

    [Fact]
    public async Task Planned_RescheduledSinceDiscovery_IsNotRemindedForTheOldDate()
    {
        var expense = AddPlanned(Date);
        var handler = Planned();
        var due = Assert.Single(await handler.FindDueAsync(_clock.UtcNow, 25, default));
        expense.Update(expense.Name, _account, _category, expense.ExpectedAmount, Date.AddDays(3), null, Due);

        var result = await handler.ExecuteAsync(Occurrence(PlannedExpenseReminderHandler.Type, due.OccurrenceKey), default);

        Assert.IsType<AutomationResult.Inapplicable>(result);
    }

    [Fact]
    public async Task Planned_Disabled_IsNotReminded_WhileRecurringStillIs()
    {
        AddPlanned(Date);
        AddRule(dayOfMonth: 15);
        _preferences.Set(UserA, recurring: true, planned: false);

        await TickAsync();

        Assert.Equal(NotificationType.RecurringTransactionReminder, Assert.Single(_deliveries.Rows).Type);
    }

    [Fact]
    public async Task Planned_InsideDefaultQuietHours_IsDelayedUntil08()
    {
        // An expense created late in the evening of its date: discovered at 22:30 CEST, sent at 08:00.
        AddPlanned(Date);
        _clock.UtcNow = new DateTimeOffset(2026, 10, 15, 20, 30, 0, TimeSpan.Zero);

        await TickAsync();
        await TickAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 6, 0, 0, TimeSpan.Zero), Assert.Single(_deliveries.Rows).NextAttemptAtUtc);

        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
        await TickAsync();

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task Planned_FailedAttempt_IsRetried_WithoutADuplicate()
    {
        AddPlanned(Date);
        _planned.Failures = 1;

        await TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await TickAsync();
        await TickAsync();

        Assert.Equal((AutomationExecutionStatus.Succeeded, 2), (Assert.Single(_executions.Rows).Status, _executions.Rows[0].AttemptCount));
        Assert.Single(_deliveries.Rows);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public void Planned_OccurrenceKey_RoundTrips()
    {
        var id = Guid.CreateVersion7();
        var key = PlannedExpenseReminderHandler.OccurrenceKey(id, Date);

        Assert.Equal($"{id:D}:2026-10-15", key);
        Assert.True(PlannedExpenseReminderHandler.TryParseOccurrenceKey(key, out var parsed, out var date));
        Assert.Equal((id, Date), (parsed, date));
        Assert.False(PlannedExpenseReminderHandler.TryParseOccurrenceKey($"{id:D}:2026-13-01", out _, out _));
    }

    // ---- Helpers ----

    private static void AssertNoFinanceDetail(PushMessage message, params string[] details)
    {
        // The copy is fixed text; the data is only a type code and an opaque id.
        var text = message.Title + " " + message.Body;

        Assert.All(details, detail => Assert.DoesNotContain(detail, text, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotMatch(@"[\d@]", text);
        Assert.Equal(["id", "type"], message.Data.Keys.Order());
        Assert.True(Guid.TryParse(message.Data["id"], out _));
    }

    private RecurringTransactionRule AddRule(int dayOfMonth)
    {
        var rule = RecurringTransactionRule.Create(UserA, "Rent", TransactionType.Expense, _account.Id, _category.Id, 900m, dayOfMonth, 2026, 1, null, Due.AddDays(-300));
        _recurring.Rules.Add(rule);
        _reminders.Recurring.Add(new DueRecurringReminder(UserA, Rome, rule.Id, Date));
        return rule;
    }

    private PlannedExpense AddPlanned(DateOnly date)
    {
        var expense = PlannedExpense.Create(UserA, "Dentist", _account, _category, 250m, date, null, Due.AddDays(-10));
        _planned.Items.Add(expense);
        _reminders.PlannedExpenses.Add(new DuePlannedExpenseReminder(UserA, Rome, expense.Id, date));
        return expense;
    }

    private RecurringTransactionReminderHandler Recurring(IRecurringRepository? recurring = null) =>
        new(_reminders, recurring ?? _recurring, _preferences, _clock);

    private PlannedExpenseReminderHandler Planned() => new(_reminders, _planned, _preferences, _clock);

    private AutomationOccurrence Occurrence(string type, string key) =>
        new(Guid.CreateVersion7(), UserA, type, key, Rome, Due, Due.AddHours(24), 1);

    private Task TickAsync(RecurringTransactionReminderHandler? recurring = null) =>
        new RunAutomationTick(
                [recurring ?? Recurring(), Planned()],
                _executions,
                new NotificationDispatcher(_deliveries, _devices, _preferences, _unitOfWork, _clock, _sender),
                _deliveries,
                _unitOfWork,
                new AutomationTickGuard(),
                _clock)
            .RunAsync();

    private sealed class InMemoryPlannedExpenses : IPlannedExpenseRepository
    {
        public List<PlannedExpense> Items { get; } = [];

        public List<PlannedExpenseState> States { get; } = [];

        // GetAsync throws this many times first (a transient read failure).
        public int Failures { get; set; }

        public Task<PlannedExpenseSnapshot> GetAsync(Guid userId, Guid id, CancellationToken ct)
        {
            if (Failures > 0)
            {
                Failures--;
                throw new InvalidOperationException("transient");
            }

            var item = Items.SingleOrDefault(candidate => candidate.UserId == userId && candidate.Id == id);
            var state = item is null ? null : States.SingleOrDefault(candidate => candidate.PlannedExpenseId == item.Id);

            return Task.FromResult(new PlannedExpenseSnapshot(item, null, null, state, null));
        }

        public Task<PlannedExpenseRead> ReadAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct) => throw new NotSupportedException();

        public Task<PlannedExpenseResult> ExecuteAsync(Guid userId, Guid? id, Guid? accountId, Guid? categoryId,
            Func<PlannedExpenseSnapshot, PlannedExpenseResult> decide, CancellationToken ct) => throw new NotSupportedException("Reminders never write.");
    }

    private sealed class FlakyRecurringRepository(IRecurringRepository inner, int failures) : IRecurringRepository
    {
        private int _failures = failures;

        public Task<RecurringRead> ReadAsync(Guid userId, int fromYear, int fromMonth, int toYear, int toMonth, CancellationToken cancellationToken,
            DateTimeOffset? transactionsFromUtc = null, DateTimeOffset? transactionsToUtc = null)
        {
            if (_failures > 0)
            {
                _failures--;
                throw new InvalidOperationException("transient");
            }

            return inner.ReadAsync(userId, fromYear, fromMonth, toYear, toMonth, cancellationToken, transactionsFromUtc, transactionsToUtc);
        }

        public Task<RecurringResult> ExecuteAsync(Guid userId, Guid? ruleId, int? year, int? month, Guid? accountId, Guid? categoryId,
            Func<RecurringSnapshot, RecurringResult> decide, CancellationToken cancellationToken) => throw new NotSupportedException("Reminders never write.");
    }
}
