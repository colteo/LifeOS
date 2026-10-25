using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Persistence;
using LifeOS.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F3: the turn processor as Program.cs composes it, from one request scope of the PostgreSQL fixture
// (real repositories, unit of work and F2 handlers). The interpreter defaults to the registered one — the
// disabled interpreter of the default configuration; tests pass a scripted one.
internal static class AssistantProcessorComposition
{
    // `operations` wraps the real operation repository (a test can inject a step at that seam).
    public static FinanceAssistantCapability Capability(IServiceProvider services, TimeProvider clock,
        Func<IFinanceOperationRepository, IFinanceOperationRepository>? operations = null)
    {
        var repository = services.GetRequiredService<IFinanceOperationRepository>();
        var operationRepository = operations is null ? repository : operations(repository);
        var conversations = services.GetRequiredService<IAssistantConversationRepository>();
        var accounts = services.GetRequiredService<IAccountRepository>();
        var categories = services.GetRequiredService<ICategoryRepository>();
        var users = services.GetRequiredService<IUserRepository>();
        var references = new FinanceDraftReferences(accounts, categories);

        return new FinanceAssistantCapability(
            new GetActiveFinanceOperationHandler(operationRepository, references, clock),
            new CreateFinanceOperationHandler(operationRepository, conversations, references, users, clock),
            new UpdateFinanceDraftHandler(operationRepository, references, users, clock),
            accounts,
            categories,
            users,
            clock);
    }

    public static AssistantTurnProcessor Processor(
        IServiceProvider services,
        TimeProvider clock,
        IAssistantInterpreter? interpreter = null,
        IAssistantConversationRepository? repository = null,
        Func<IAssistantCapability, IAssistantCapability>? capability = null,
        Func<IFinanceOperationRepository, IFinanceOperationRepository>? operations = null)
    {
        var finance = Capability(services, clock, operations);

        return new AssistantTurnProcessor(
            repository ?? services.GetRequiredService<IAssistantConversationRepository>(),
            interpreter ?? services.GetRequiredService<IAssistantInterpreter>(),
            capability is null ? finance : capability(finance),
            services.GetRequiredService<IUnitOfWork>(),
            clock);
    }
}
