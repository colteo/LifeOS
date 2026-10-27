using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Nutrition;
using LifeOS.Application.Nutrition.Assistant;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Application.Nutrition.Hydration.Assistant;
using LifeOS.Application.Persistence;
using LifeOS.Application.Users;
using LifeOS.Domain.Nutrition.Assistant;
using LifeOS.Domain.Nutrition.Hydration.Assistant;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F3 / AI-008: the turn processor as Program.cs composes it, from one request scope of the PostgreSQL fixture
// (real repositories, unit of work and handlers): the capability router over the Finance module and, when a test
// asks for them, the water and meal modules. The interpreter defaults to the registered one — the disabled
// interpreter of the default configuration; tests pass a scripted one.
internal static class AssistantProcessorComposition
{
    // The core's replies when Finance is the only module (the AI-007 texts).
    public static readonly AssistantCoreReplies FinanceOnlyReplies = new(["spese ed entrate"]);

    // `operations` wraps the real operation repository (a test can inject a step at that seam).
    public static FinanceAssistantModule Finance(IServiceProvider services, TimeProvider clock,
        Func<IFinanceOperationRepository, IFinanceOperationRepository>? operations = null)
    {
        var repository = services.GetRequiredService<IFinanceOperationRepository>();
        var operationRepository = operations is null ? repository : operations(repository);
        var conversations = services.GetRequiredService<IAssistantConversationRepository>();
        var accounts = services.GetRequiredService<IAccountRepository>();
        var categories = services.GetRequiredService<ICategoryRepository>();
        var users = services.GetRequiredService<IUserRepository>();
        var references = new FinanceDraftReferences(accounts, categories);

        return new FinanceAssistantModule(
            new GetActiveFinanceOperationHandler(operationRepository, references, clock),
            new CreateFinanceOperationHandler(operationRepository, conversations, references, users, clock),
            new UpdateFinanceDraftHandler(operationRepository, references, users, clock),
            accounts,
            categories,
            users,
            clock);
    }

    // The water drafts' use cases over the real lifecycle; the confirmation goes through AddHydrationIntakeHandler.
    public static HydrationAssistantOperations HydrationOperations(IServiceProvider services, TimeProvider clock)
    {
        var hydration = services.GetRequiredService<IHydrationRepository>();
        var policy = new HydrationDraftPolicy(new AddHydrationIntakeHandler(hydration, new HydrationDayReader(hydration), clock));

        return new HydrationAssistantOperations(Lifecycle(services, policy, clock), services.GetRequiredService<IUserRepository>(), clock);
    }

    // The meal drafts' use cases over the real lifecycle; the confirmation goes through CreateMealHandler.
    public static MealAssistantOperations MealOperations(IServiceProvider services, TimeProvider clock)
    {
        var policy = new MealDraftPolicy(new CreateMealHandler(services.GetRequiredService<IMealEntryRepository>(), clock));

        return new MealAssistantOperations(Lifecycle(services, policy, clock), services.GetRequiredService<IUserRepository>(), clock);
    }

    public static HydrationAssistantModule Hydration(IServiceProvider services, TimeProvider clock) =>
        new(HydrationOperations(services, clock), services.GetRequiredService<IUserRepository>(), clock);

    public static MealAssistantModule Meals(IServiceProvider services, TimeProvider clock) =>
        new(MealOperations(services, clock), services.GetRequiredService<IUserRepository>(), clock);

    private static AssistantOperationLifecycle<TDraft> Lifecycle<TDraft>(IServiceProvider services, IAssistantDraftPolicy<TDraft> policy,
        TimeProvider clock) where TDraft : class, LifeOS.Domain.Assistant.IAssistantDraft =>
        new(services.GetRequiredService<IAssistantOperationRepository<TDraft>>(), services.GetRequiredService<IAssistantOperationReader>(),
            services.GetRequiredService<IAssistantConversationRepository>(), policy, services.GetRequiredService<IUnitOfWork>(), clock);

    // Finance + water + meals, as in Development.
    public static IReadOnlyList<IAssistantModule> AllModules(IServiceProvider services, TimeProvider clock) =>
        [Finance(services, clock), Hydration(services, clock), Meals(services, clock)];

    public static AssistantTurnProcessor Processor(
        IServiceProvider services,
        TimeProvider clock,
        IAssistantInterpreter? interpreter = null,
        IAssistantConversationRepository? repository = null,
        Func<IAssistantCapability, IAssistantCapability>? capability = null,
        Func<IFinanceOperationRepository, IFinanceOperationRepository>? operations = null,
        IReadOnlyList<IAssistantModule>? modules = null)
    {
        var router = new AssistantCapabilityRouter(services.GetRequiredService<IAssistantOperationReader>(),
            modules ?? [Finance(services, clock, operations)]);

        return new AssistantTurnProcessor(
            repository ?? services.GetRequiredService<IAssistantConversationRepository>(),
            interpreter ?? services.GetRequiredService<IAssistantInterpreter>(),
            capability is null ? router : capability(router),
            services.GetRequiredService<IUnitOfWork>(),
            clock);
    }
}
