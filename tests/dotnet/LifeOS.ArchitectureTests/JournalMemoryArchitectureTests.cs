using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using LifeOS.Application.Journal;
using LifeOS.Application.Memory;
using LifeOS.Application.Persistence;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// AI-004: the Journal Memory Layer's boundaries, machine-enforced. The journal is the source of truth
// and knows nothing about memory or AI; journal writes only record an index request (database work);
// the memory use cases read the derived index and call the AI ports, with no path to any journal or
// other-module write; search cannot reach the answer model; the AI ports live only in Infrastructure.
// (The Python side, stateless and DB-free, and the evaluation lab's absence from production imports are
// enforced by src/python/lifeos-ai/tests/test_architecture.py.)
public class JournalMemoryArchitectureTests
{
    private static readonly Assembly Application = typeof(SyncJournalMemoryHandler).Assembly;

    private static readonly string[] OtherModules =
    [
        "LifeOS.Application.Finance", "LifeOS.Application.Gym", "LifeOS.Application.Nutrition",
        "LifeOS.Application.WeeklyReviews", "LifeOS.Application.ActionAgent",
        "LifeOS.Domain.Finance", "LifeOS.Domain.Gym", "LifeOS.Domain.Nutrition",
        "LifeOS.Domain.WeeklyReviews", "LifeOS.Domain.ActionAgent"
    ];

    [Fact]
    public void The_Journal_Domain_Knows_No_Memory_Ai_Or_Persistence()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Domain")).That().ResideInNamespace("LifeOS.Domain.Journal")
            .ShouldNot().HaveDependencyOnAny("LifeOS.Application", "LifeOS.Infrastructure", "Npgsql", "Microsoft.EntityFrameworkCore", "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // Journal CRUD cannot reach the network or any AI port: the Journal use cases do not depend on the
    // memory namespace at all, and writes depend only on the repository, the index queue (database),
    // the unit of work and the clock.
    [Fact]
    public void Journal_Crud_Cannot_Reach_Ai_Or_Memory_Use_Cases()
    {
        var result = Types.InAssembly(Application).That().ResideInNamespace("LifeOS.Application.Journal")
            .ShouldNot().HaveDependencyOnAny("LifeOS.Application.Memory", "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));

        Type[] writes = [typeof(IJournalEntryRepository), typeof(IJournalIndexQueue), typeof(IUnitOfWork), typeof(TimeProvider)];
        Assert.Equal(writes, Constructor<CreateJournalEntryHandler>());
        Assert.Equal(writes, Constructor<UpdateJournalEntryHandler>());
        Assert.Equal([typeof(IJournalEntryRepository)], Constructor<DeleteJournalEntryHandler>());
    }

    // The memory use cases read the derived index and call the AI ports. They hold no journal
    // repository, index queue or unit of work: no path to write a journal entry.
    [Fact]
    public void Memory_Use_Cases_Cannot_Write_The_Journal()
    {
        Assert.Equal([typeof(IJournalMemoryStore)], Constructor<GetJournalMemoryStatusHandler>());
        Assert.Equal([typeof(IJournalMemoryStore), typeof(IJournalEmbeddingService), typeof(TimeProvider)], Constructor<SyncJournalMemoryHandler>());
        Assert.Equal([typeof(IJournalMemoryStore), typeof(IJournalEmbeddingService)], Constructor<JournalMemoryRetrieval>());
        Assert.Equal([typeof(JournalMemoryRetrieval)], Constructor<SearchJournalMemoryHandler>());
        Assert.Equal([typeof(JournalMemoryRetrieval), typeof(IJournalAnswerService)], Constructor<AskJournalMemoryHandler>());

        var result = Types.InAssembly(Application).That().ResideInNamespace("LifeOS.Application.Memory")
            .ShouldNot().HaveDependencyOnAny(
                "LifeOS.Application.Journal.IJournalEntryRepository",
                "LifeOS.Application.Journal.IJournalIndexQueue",
                "LifeOS.Application.Persistence.IUnitOfWork",
                "LifeOS.Domain.Journal.JournalEntry")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));

        // The store's surface: derived index only, nothing that edits an entry.
        Assert.Equal(["CompleteAsync", "GetCountsAsync", "ReleaseAsync", "SearchAsync", "TryClaimNextAsync"],
            typeof(IJournalMemoryStore).GetMethods().Select(method => method.Name).Order());
    }

    // Search is retrieval only; only ask reaches the answer model.
    [Fact]
    public void Search_Cannot_Reach_The_Answer_Model()
    {
        var result = Types.InAssembly(Application)
            .That().HaveName(nameof(SearchJournalMemoryHandler)).Or().HaveName(nameof(JournalMemoryRetrieval))
            .Or().HaveName(nameof(SyncJournalMemoryHandler)).Or().HaveName(nameof(GetJournalMemoryStatusHandler))
            .ShouldNot().HaveDependencyOn(typeof(IJournalAnswerService).FullName)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // Memory (every layer) has no path to Finance, Gym, Nutrition, Weekly Review or Action Agent code:
    // the answer is text with citations, never an action.
    [Theory]
    [InlineData("LifeOS.Application", "LifeOS.Application.Memory")]
    [InlineData("LifeOS.Infrastructure", "LifeOS.Infrastructure.Memory")]
    [InlineData("LifeOS.Contracts", "LifeOS.Contracts.Memory")]
    [InlineData("LifeOS.Api", "LifeOS.Api.Memory")]
    public void Memory_Has_No_Path_To_Other_Modules(string assemblyName, string memoryNamespace)
    {
        var types = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(memoryNamespace);
        Assert.NotEmpty(types.GetTypes());

        var result = types.ShouldNot().HaveDependencyOnAny(
                [.. OtherModules, .. OtherModules.Select(name => name.Replace("LifeOS.Application", "LifeOS.Infrastructure", StringComparison.Ordinal))])
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData(typeof(IJournalEmbeddingService))]
    [InlineData(typeof(IJournalAnswerService))]
    [InlineData(typeof(IJournalMemoryStore))]
    [InlineData(typeof(IJournalIndexQueue))]
    public void Memory_Ports_Are_Implemented_Only_In_Infrastructure_Memory(Type port)
    {
        Assert.True(port.IsInterface);

        var implementations = new[] { "LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Contracts", "LifeOS.Api" }
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(port).GetTypes())
            .ToList();

        var implementation = Assert.Single(implementations);
        Assert.Equal("LifeOS.Infrastructure.Memory", implementation.Namespace);
    }

    [Fact]
    public void Memory_Application_Code_Knows_No_Provider_Framework_Or_Transport()
    {
        var result = Types.InAssembly(Application).That().ResideInNamespace("LifeOS.Application.Memory")
            .ShouldNot().HaveDependencyOnAny("System.Net.Http", "Npgsql", "Pgvector", "Microsoft.EntityFrameworkCore", "OpenAI", "Google", "Gemini", "Groq")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The embedding provider is an outer adapter (the Python service). Its identity reaches .NET only as
    // the pinned index identity strings (data the index and query answers are validated against); the
    // schema, the vector size and the retrieval policy do not depend on it. Swapping OpenAI for Gemini
    // (same 1536 dimensions) changed only those strings: the AI-004 migration, including
    // search_journal_memory_v1 and its RRF constants, is byte-for-byte the one first shipped.
    [Fact]
    public void Embedding_Provider_Swap_Leaves_Schema_Dimensions_And_Retrieval_Unchanged()
    {
        Assert.Equal(1536, JournalMemoryPolicy.EmbeddingDimensions);
        Assert.Equal("journal-chunking-v1", JournalMemoryPolicy.ChunkingVersion);
        Assert.Equal("journal-retrieval-v1", JournalMemoryPolicy.RetrievalVersion);
        Assert.Equal("search_journal_memory_v1", JournalMemoryPolicy.RetrievalFunction);

        // Line endings normalized: the hash must not depend on the checkout's autocrlf setting.
        var normalized = File.ReadAllText(MigrationFile()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal("a5d9e472f543e29b10c0b1a8e158b1c512b187425462fed6ce0b98c06bf1969e",
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))));
        Assert.Contains("embedding            vector(1536)  NOT NULL", normalized);
        Assert.Contains("vector_dims(p_query_embedding) <> 1536", normalized);
        // Provider and model are row data and function arguments, never schema.
        foreach (var provider in new[] { "openai", "text-embedding", "google", "gemini" })
        {
            Assert.DoesNotContain(provider, normalized, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string MigrationFile([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "dotnet",
            "LifeOS.Infrastructure", "Persistence", "Migrations", "20261008171403_AddJournalMemory.cs"));

    private static Type[] Constructor<T>() =>
        typeof(T).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType).ToArray();
}
