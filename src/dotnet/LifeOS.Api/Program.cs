using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Api.ActionAgent;
using LifeOS.Api.Assistant;
using LifeOS.Api.Authentication;
using LifeOS.Api.Automation;
using LifeOS.Api.Finance;
using LifeOS.Api.Gym;
using LifeOS.Api.Habits;
using LifeOS.Api.Health;
using LifeOS.Api.Journal;
using LifeOS.Api.Memory;
using LifeOS.Api.Notifications;
using LifeOS.Api.Nutrition;
using LifeOS.Api.Onboarding;
using LifeOS.Api.Reminders;
using LifeOS.Api.Users;
using LifeOS.Api.WeeklyReviews;
using LifeOS.Application.Authentication.RefreshSession;
using LifeOS.Application.Authentication.RevokeSession;
using LifeOS.Application.Authentication.StartSession;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Application.Finance.Accounts.SetOpeningBalance;
using LifeOS.Application.Finance.Accounts.UpdateAccount;
using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.DeleteCategory;
using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Application.Finance.Categories.RenameCategory;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Finance.Transactions.DeleteTransaction;
using LifeOS.Application.Finance.Transactions.GetRecentTransactions;
using LifeOS.Application.Finance.Transactions.GetTransaction;
using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Application.Finance.Transactions.UpdateTransaction;
using LifeOS.Application.Gym.Exercises.CreateExercise;
using LifeOS.Application.Gym.Exercises.GetExercises;
using LifeOS.Application.Gym.History;
using LifeOS.Application.Gym.Programs.Blocks;
using LifeOS.Application.Gym.Programs.CreateWorkoutProgram;
using LifeOS.Application.Gym.Programs.DeleteWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutPrograms;
using LifeOS.Application.Gym.Programs.RenameWorkoutProgram;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Gym.Training;
using LifeOS.Application.Journal;
using LifeOS.Application.Notifications.Devices;
using LifeOS.Application.Nutrition;
using LifeOS.Application.Onboarding.CompleteOnboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Application.Users.SignInWithExternalIdentity;
using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Infrastructure;
using LifeOS.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration
    .GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException(
        "Connection string 'PostgreSQL' not found.");

// NUT-002: the Python AI service is optional; without NutritionAi:BaseUrl estimates are unavailable.
// AUTO-001: push only when FCM is configured (both values, or neither: see PushNotificationsConfiguration).
// AI-007 F3: the assistant's interpreter stays disabled unless AssistantAi:Enabled is set explicitly.
var nutritionAi = NutritionAiConfiguration.Read(builder.Configuration);
builder.Services.AddInfrastructure(
    connectionString,
    nutritionAi,
    PushNotificationsConfiguration.ReadFcm(builder.Configuration),
    AssistantAiConfiguration.Read(builder.Configuration, nutritionAi));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CreateAccountHandler>();
builder.Services.AddScoped<GetAccountsHandler>();
builder.Services.AddScoped<SetOpeningBalanceHandler>();
builder.Services.AddScoped<UpdateAccountHandler>();
builder.Services.AddScoped<DeleteAccountHandler>();
builder.Services.AddScoped<GetAccountBalancesHandler>();
builder.Services.AddScoped<ReconcileAccountHandler>();
builder.Services.AddScoped<CreateCategoryHandler>();
builder.Services.AddScoped<GetCategoriesHandler>();
builder.Services.AddScoped<RenameCategoryHandler>();
builder.Services.AddScoped<DeleteCategoryHandler>();
builder.Services.AddScoped<CreateTransactionHandler>();
builder.Services.AddScoped<GetTransactionsHandler>();
builder.Services.AddScoped<GetRecentTransactionsHandler>();
builder.Services.AddScoped<GetTransactionHandler>();
builder.Services.AddScoped<UpdateTransactionHandler>();
builder.Services.AddScoped<DeleteTransactionHandler>();
builder.Services.AddScoped<GetMonthlyAnalyticsHandler>();
builder.Services.AddScoped<GetMonthlyBudgetHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Recurring.RecurringHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.PlannedExpenses.PlannedExpenseHandler>();
builder.Services.AddScoped<SetMonthlyBudgetHandler>();
builder.Services.AddScoped<DeleteMonthlyBudgetHandler>();
builder.Services.AddScoped<CreateExerciseHandler>();
builder.Services.AddScoped<GetExercisesHandler>();
builder.Services.AddScoped<GetWorkoutProgramsHandler>();
builder.Services.AddScoped<GetWorkoutProgramHandler>();
builder.Services.AddScoped<CreateWorkoutProgramHandler>();
builder.Services.AddScoped<RenameWorkoutProgramHandler>();
builder.Services.AddScoped<DeleteWorkoutProgramHandler>();
builder.Services.AddScoped<AddWorkoutHandler>();
builder.Services.AddScoped<RenameWorkoutHandler>();
builder.Services.AddScoped<DeleteWorkoutHandler>();
builder.Services.AddScoped<ReorderWorkoutsHandler>();
builder.Services.AddScoped<AddWorkoutBlockHandler>();
builder.Services.AddScoped<UpdateWorkoutBlockHandler>();
builder.Services.AddScoped<DeleteWorkoutBlockHandler>();
builder.Services.AddScoped<ReorderWorkoutBlocksHandler>();
builder.Services.AddScoped<StartWorkoutSessionHandler>();
builder.Services.AddScoped<GetWorkoutSessionHandler>();
builder.Services.AddScoped<RecordWorkoutSetHandler>();
builder.Services.AddScoped<FinishWorkoutSessionHandler>();
builder.Services.AddScoped<DiscardWorkoutSessionHandler>();
builder.Services.AddScoped<GetTrainingProgramsHandler>();
builder.Services.AddScoped<ActiveProgramProgress>();
builder.Services.AddScoped<GetActiveProgramHandler>();
builder.Services.AddScoped<ActivateProgramHandler>();
builder.Services.AddScoped<StopActiveProgramHandler>();
builder.Services.AddScoped<GetWorkoutHistoryHandler>();
builder.Services.AddScoped<GetWorkoutHistoryDetailHandler>();
builder.Services.AddScoped<GetPreviousPerformanceHandler>();
builder.Services.AddScoped<GetExerciseHistoryHandler>();
builder.Services.AddScoped<GetMealsForDateHandler>();
builder.Services.AddScoped<CreateMealHandler>();
builder.Services.AddScoped<UpdateMealHandler>();
builder.Services.AddScoped<DeleteMealHandler>();
builder.Services.AddScoped<MealNutritionEstimation>();
builder.Services.AddScoped<EstimateMealNutritionHandler>();
builder.Services.AddScoped<SetMealNutritionHandler>();
builder.Services.AddScoped<GetDailyNutritionSummaryHandler>();
builder.Services.AddScoped<AnalyzeDayHandler>();
builder.Services.AddScoped<LazyCloseNutritionHandler>();
builder.Services.AddScoped<GetNutritionTargetPlansHandler>();
builder.Services.AddScoped<GetNutritionTargetPlanHandler>();
builder.Services.AddScoped<SaveNutritionTargetPlanHandler>();
builder.Services.AddScoped<DeleteNutritionTargetPlanHandler>();
builder.Services.AddScoped<GetResolvedNutritionTargetHandler>();
builder.Services.AddScoped<SetNutritionTargetOverrideHandler>();
builder.Services.AddScoped<RemoveNutritionTargetOverrideHandler>();

// HYD-001: Hydration, the single source of truth for water. IHydrationDayReader is its only read surface
// for other modules; totals and target status are computed on every read.
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.HydrationDayReader>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.IHydrationDayReader>(services =>
    services.GetRequiredService<LifeOS.Application.Nutrition.Hydration.HydrationDayReader>());
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.AddHydrationIntakeHandler>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.UpdateHydrationIntakeHandler>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.DeleteHydrationIntakeHandler>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.GetHydrationDayHandler>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.GetHydrationDaysHandler>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.GetHydrationSettingsHandler>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.SetHydrationTargetHandler>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.SetHydrationQuickAddHandler>();

builder.Services.AddLifeOSAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddScoped<SignInWithExternalIdentityHandler>();
builder.Services.AddScoped<StartSessionHandler>();
builder.Services.AddScoped<RefreshSessionHandler>();
builder.Services.AddScoped<RevokeSessionHandler>();
builder.Services.AddScoped<GetCurrentUserHandler>();
builder.Services.AddScoped<SetTimeZoneHandler>();
builder.Services.AddScoped<SetUpFinanceProfileHandler>();
builder.Services.AddScoped<CompleteOnboardingHandler>();
builder.Services.AddScoped<RegisterDeviceHandler>();
builder.Services.AddScoped<UnregisterDeviceHandler>();

// Notification dispatch: disabled while no push sender is registered (FCM not configured).
builder.Services.AddScoped<LifeOS.Application.Notifications.NotificationDispatcher>();
builder.Services.AddScoped<LifeOS.Application.Notifications.SendTestNotificationHandler>();
builder.Services.AddTestNotificationRateLimit();

// AUTO-003A: reminder preferences and quiet hours (one optional row per user).
builder.Services.AddScoped<LifeOS.Application.Notifications.GetNotificationPreferencesHandler>();
builder.Services.AddScoped<LifeOS.Application.Notifications.SetNotificationPreferencesHandler>();

// AUTO-002: saved weekly reviews and their module-owned enabled setting.
builder.Services.AddScoped<GetWeeklyReviewsHandler>();
builder.Services.AddScoped<GetWeeklyReviewHandler>();
builder.Services.AddScoped<GetWeeklyReviewSettingsHandler>();
builder.Services.AddScoped<SetWeeklyReviewSettingsHandler>();

// JRN-001: the personal journal.
builder.Services.AddScoped<GetJournalEntriesHandler>();
builder.Services.AddScoped<GetJournalEntryHandler>();
builder.Services.AddScoped<CreateJournalEntryHandler>();
builder.Services.AddScoped<UpdateJournalEntryHandler>();
builder.Services.AddScoped<DeleteJournalEntryHandler>();

// HAB-001: the Habit Tracker. Deterministic statistics, computed on every read; the water habit (B4) reads
// Hydration only through IHydrationDayReader. IHabitDayStatusReader is the read port for Reminders (R5).
builder.Services.AddScoped<LifeOS.Application.Habits.GetHabitsForDayHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.GetArchivedHabitsHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.GetHabitDetailHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.GetHabitHistoryHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.CreateHabitHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.UpdateHabitHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.ArchiveHabitHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.DeleteHabitHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.SetHabitCheckInHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.RemoveHabitCheckInHandler>();
builder.Services.AddScoped<LifeOS.Application.Habits.IHabitDayStatusReader, LifeOS.Application.Habits.HabitDayStatusReader>();

// REM-001: the user's own reminders (once, daily, weekly), with pause, resume and snooze, and (R5) the
// conditional habit and water nudges. Delivery is the ReminderAutomationHandler /
// ConditionalReminderAutomationHandler below (registered only with the tick key, like AUTO-003A). The
// nudges' condition is one evaluator (Habits and Hydration read through their ports), used when an
// occurrence runs and, through the notification core's relevance check, right before every send attempt.
builder.Services.AddScoped<LifeOS.Application.Reminders.GetRemindersHandler>();
builder.Services.AddScoped<LifeOS.Application.Reminders.GetReminderHandler>();
builder.Services.AddScoped<LifeOS.Application.Reminders.CreateReminderHandler>();
builder.Services.AddScoped<LifeOS.Application.Reminders.UpdateReminderHandler>();
builder.Services.AddScoped<LifeOS.Application.Reminders.PauseReminderHandler>();
builder.Services.AddScoped<LifeOS.Application.Reminders.SnoozeReminderHandler>();
builder.Services.AddScoped<LifeOS.Application.Reminders.DeleteReminderHandler>();
builder.Services.AddScoped<LifeOS.Application.Reminders.ReminderConditionEvaluator>();
builder.Services.AddScoped<LifeOS.Application.Notifications.INotificationRelevanceCheck, LifeOS.Application.Reminders.ReminderRelevanceCheck>();

// AI-004: the Journal Memory Layer (derived index, hybrid retrieval, grounded answers). Read-only towards
// the journal; indexing runs only through the explicit, bounded sync.
builder.Services.AddScoped<LifeOS.Application.Memory.GetJournalMemoryStatusHandler>();
builder.Services.AddScoped<LifeOS.Application.Memory.SyncJournalMemoryHandler>();
builder.Services.AddScoped<LifeOS.Application.Memory.JournalMemoryRetrieval>();
builder.Services.AddScoped<LifeOS.Application.Memory.SearchJournalMemoryHandler>();
builder.Services.AddScoped<LifeOS.Application.Memory.AskJournalMemoryHandler>();

// AI-001: AI Insights of a saved weekly review, generated on demand only (never by the automation).
builder.Services.AddScoped<GetWeeklyReviewInsightsHandler>();
builder.Services.AddScoped<GenerateWeeklyReviewInsightsHandler>();

// AI-002: the Action Agent. Read-only tools and the agent run (which stores at most a Pending proposal);
// the budget is changed only by the user's explicit approval, through SetMonthlyBudgetHandler.
builder.Services.AddScoped<LifeOS.Application.ActionAgent.ActionAgentTools>();
builder.Services.AddScoped<LifeOS.Application.ActionAgent.RunActionAgentHandler>();
builder.Services.AddScoped<LifeOS.Application.ActionAgent.GetReviewProposedActionHandler>();
builder.Services.AddScoped<LifeOS.Application.ActionAgent.GetProposedActionHandler>();
builder.Services.AddScoped<LifeOS.Application.ActionAgent.ApproveProposedActionHandler>();
builder.Services.AddScoped<LifeOS.Application.ActionAgent.RejectProposedActionHandler>();

// AI-007 F1/F3: LifeOS Assistant conversations and messages. Turns are reached only through
// AssistantTurnProcessor, which composes the turn handlers itself, interprets the message (IAssistantInterpreter,
// disabled by default: every attempt then fails with assistant_unavailable) and applies the result through the
// capability router (AI-008). The handlers that accept or close a turn are deliberately not registered, and the
// HTTP adapter cannot reference them (AssistantArchitectureTests).
builder.Services.AddScoped<LifeOS.Application.Assistant.CreateAssistantConversationHandler>();
builder.Services.AddScoped<LifeOS.Application.Assistant.ListAssistantConversationsHandler>();
builder.Services.AddScoped<LifeOS.Application.Assistant.GetAssistantConversationMessagesHandler>();
builder.Services.AddScoped<LifeOS.Application.Assistant.DeleteAssistantConversationHandler>();
builder.Services.AddScoped<LifeOS.Application.Assistant.AssistantTurnProcessor>();

// AI-007 F2: assistant operations (v1: one Expense or Income). Drafts never write Finance; the only Finance
// write is ConfirmFinanceOperationHandler, through CreateTransactionHandler inside one unit of work.
builder.Services.AddScoped<LifeOS.Application.Assistant.GetAssistantConversationHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Assistant.FinanceDraftReferences>();
builder.Services.AddScoped<LifeOS.Application.Finance.Assistant.CreateFinanceOperationHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Assistant.GetFinanceOperationHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Assistant.GetActiveFinanceOperationHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Assistant.UpdateFinanceDraftHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Assistant.ConfirmFinanceOperationHandler>();
builder.Services.AddScoped<LifeOS.Application.Finance.Assistant.CancelFinanceOperationHandler>();

// AI-008: water intakes and meals share the operation lifecycle; their only module writes are the confirmations,
// through AddHydrationIntakeHandler and CreateMealHandler inside one unit of work. Their operations stay readable,
// confirmable and cancellable whether or not their module is enabled in the conversation.
builder.Services.AddScoped<LifeOS.Application.Assistant.IAssistantDraftPolicy<LifeOS.Domain.Nutrition.Hydration.Assistant.HydrationIntakeDraft>,
    LifeOS.Application.Nutrition.Hydration.Assistant.HydrationDraftPolicy>();
builder.Services.AddScoped<LifeOS.Application.Assistant.AssistantOperationLifecycle<LifeOS.Domain.Nutrition.Hydration.Assistant.HydrationIntakeDraft>>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Hydration.Assistant.HydrationAssistantOperations>();
builder.Services.AddScoped<LifeOS.Application.Assistant.IAssistantDraftPolicy<LifeOS.Domain.Nutrition.Assistant.MealDraft>,
    LifeOS.Application.Nutrition.Assistant.MealDraftPolicy>();
builder.Services.AddScoped<LifeOS.Application.Assistant.AssistantOperationLifecycle<LifeOS.Domain.Nutrition.Assistant.MealDraft>>();
builder.Services.AddScoped<LifeOS.Application.Nutrition.Assistant.MealAssistantOperations>();

// AI-007 F3 / AI-008: the modules of the conversation, behind the router the turn processor sees. Each prepares
// drafts only through its handlers above, never the confirmation. Finance is always on; water and meals are on in
// Development only unless Assistant:Modules:Hydration / Assistant:Modules:NutritionMeals say otherwise (ADR-014
// addendum: not released yet). A disabled module's intents are answered "not available".
var assistantModules = AssistantModulesConfiguration.Read(builder.Configuration, builder.Environment);
builder.Services.AddScoped<LifeOS.Application.Assistant.IAssistantModule, LifeOS.Application.Finance.Assistant.FinanceAssistantModule>();

if (assistantModules.Hydration)
{
    builder.Services.AddScoped<LifeOS.Application.Assistant.IAssistantModule, LifeOS.Application.Nutrition.Hydration.Assistant.HydrationAssistantModule>();
}

if (assistantModules.NutritionMeals)
{
    builder.Services.AddScoped<LifeOS.Application.Assistant.IAssistantModule, LifeOS.Application.Nutrition.Assistant.MealAssistantModule>();
}

builder.Services.AddScoped<LifeOS.Application.Assistant.IAssistantCapability, LifeOS.Application.Assistant.AssistantCapabilityRouter>();

// AUTO-001: enabled only when Automation:TickKey is configured (validated, with a tzdata check).
var automation = AutomationConfiguration.Read(builder.Configuration);

if (automation is not null)
{
    builder.Services.AddLifeOSAutomation(automation);

    // AUTO-002: the only business automation (Sunday 20:00 local). It reads Finance, Gym and
    // Nutrition through their own use cases and never mutates them.
    builder.Services.AddScoped<WeeklyReviewSnapshotBuilder>();
    builder.Services.AddScoped<LifeOS.Application.Automation.IAutomationHandler, WeeklyReviewAutomationHandler>();

    // AUTO-003A: Finance reminders (09:00 local on the scheduled date). Read-only towards Finance; the
    // notification goes through the outbox and waits for the end of the user's quiet hours.
    builder.Services.AddScoped<LifeOS.Application.Automation.IAutomationHandler, LifeOS.Application.Finance.Reminders.RecurringTransactionReminderHandler>();
    builder.Services.AddScoped<LifeOS.Application.Automation.IAutomationHandler, LifeOS.Application.Finance.Reminders.PlannedExpenseReminderHandler>();

    // REM-001: the user's own reminders (local wall-clock times in the user's zone, 6 h lateness). Same
    // engine, outbox and quiet hours; the push carries fixed copy and the reminder id only.
    builder.Services.AddScoped<LifeOS.Application.Automation.IAutomationHandler, LifeOS.Application.Reminders.ReminderAutomationHandler>();

    // REM-001 R5: the habit and water nudges (2 h lateness, 3 h delivery expiry), sent only while needed.
    builder.Services.AddScoped<LifeOS.Application.Automation.IAutomationHandler, LifeOS.Application.Reminders.ConditionalReminderAutomationHandler>();
}

var developmentSignInEnabled = DevelopmentSignIn.IsEnabled(builder);
var googleSignInEnabled = GoogleSignIn.IsEnabled(builder.Configuration, builder.Environment);

// Outside Development the API runs behind Render's edge / load-balancer proxy, which terminates TLS
// and forwards plain HTTP with X-Forwarded-Proto / X-Forwarded-For. Without these headers the API
// would see http://, so Google sign-in would build http://…/signin-google and cookies would not be
// Secure. Render's proxy addresses are not published, so no proxy address is pinned (the
// loopback-only defaults would ignore every proxied request). This is safe only because the service
// is reachable solely through Render's public edge proxy (Render Free web services cannot receive
// private-network traffic), which writes the real values; ForwardLimit = 1 uses only the last
// (proxy-written) entry, never one supplied by the client. Host is not taken from headers.
// If the service moves away from Render Free, or private networking is ever used, review this trust
// configuration. Development has no proxy and ignores these headers.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi()
        .AllowAnonymous();
}

// First, so everything after it (HTTPS redirection, authentication including the Google
// /signin-google callback, redirects) sees the original scheme and client address.
if (!app.Environment.IsDevelopment())
{
    app.UseForwardedHeaders();
}

app.UseHttpsRedirection();

// Explicit, so authentication runs after the forwarded headers (when implicit, ASP.NET Core adds it
// at the start of the pipeline, before them).
app.UseAuthentication();
app.UseAuthorization();

// After authentication: the test-notification limit is partitioned by user.
app.UseRateLimiter();

// PROD-AI-001: anonymous liveness (process only, never the database), every environment.
app.MapHealthEndpoints();

// Development-only connectivity check for local setup (docs/development/local-development.md).
// Not mapped elsewhere: anonymous polling would keep the database awake.
if (app.Environment.IsDevelopment())
{
    app.MapGet("/health/database", async (LifeOSDbContext dbContext) =>
    {
        var canConnect = await dbContext.Database.CanConnectAsync();

        return canConnect
            ? Results.Ok(new { database = "connected" })
            : Results.Problem("Database connection failed.");
    })
    .AllowAnonymous();
}

app.MapAccountEndpoints();
app.MapAccountReconciliationEndpoints();
app.MapCategoryEndpoints();
app.MapTransactionEndpoints();
app.MapAnalyticsEndpoints();
app.MapBudgetEndpoints();
app.MapRecurringEndpoints();
app.MapPlannedExpenseEndpoints();
app.MapExerciseEndpoints();
app.MapWorkoutProgramEndpoints();
app.MapWorkoutSessionEndpoints();
app.MapTrainingEndpoints();
app.MapActiveProgramEndpoints();
app.MapWorkoutHistoryEndpoints();
app.MapNutritionEndpoints();
app.MapNutritionAnalysisEndpoints();
app.MapNutritionTargetEndpoints();
app.MapHydrationEndpoints();
app.MapAuthEndpoints(developmentSignInEnabled);

if (googleSignInEnabled)
{
    app.MapGoogleSignInEndpoints();
}
app.MapMeEndpoints();
app.MapOnboardingEndpoints();
app.MapDeviceEndpoints();
app.MapNotificationEndpoints();
app.MapNotificationPreferenceEndpoints();
app.MapWeeklyReviewEndpoints();
app.MapJournalEndpoints();
app.MapHabitEndpoints();
app.MapReminderEndpoints();
app.MapJournalMemoryEndpoints();
app.MapActionAgentEndpoints();
app.MapAssistantEndpoints();

// Not mapped when automation is disabled (no tick key configured).
if (automation is not null)
{
    app.MapAutomationTickEndpoints();
}

app.Run();

// Exposes the entry point to WebApplicationFactory in LifeOS.IntegrationTests.
public partial class Program;
