using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.Api.Nutrition;
using LifeOS.Application.Memory;
using Microsoft.Extensions.Configuration;

namespace LifeOS.UnitTests.Memory;

// AI-006.1: Ask Journal makes two sequential calls to the AI service (query embedding, then the answer),
// each bounded by the shared NutritionAi:TimeoutSeconds. The promoted OpenAI answer adapter can take up
// to 3 × 30 s plus 2 × 4 s of waits (98 s; lifeos-ai tests/test_journal_openai_answer.py), which the
// 60 s code default would cut off. Production sets 120 s explicitly (docs/operations/production-runbook.md),
// so the global default stays unchanged and the Production value is pinned here instead.
public class JournalAskTimeoutTests
{
    private static readonly TimeSpan RequiredForJournalAsk = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan OpenAIAnswerWorstCase = TimeSpan.FromSeconds(3 * 30 + 2 * 4);

    [Fact]
    public void TheProductionRunbook_SetsTheAiServiceTimeoutToAtLeast120Seconds()
    {
        var runbook = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operations", "production-runbook.md"));

        var setting = Regex.Match(runbook, @"^\| `NutritionAi__TimeoutSeconds` \| Not secret \| `(\d+)`", RegexOptions.Multiline);
        Assert.True(setting.Success, "The runbook no longer lists NutritionAi__TimeoutSeconds for lifeos-api.");

        var production = NutritionAiConfiguration.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [NutritionAiConfiguration.TimeoutSecondsKey] = setting.Groups[1].Value })
            .Build()).Timeout;

        Assert.True(production >= RequiredForJournalAsk);
        Assert.True(production > OpenAIAnswerWorstCase);
        // A claimed index job's lease still outlasts one AI-service call.
        Assert.True(JournalMemoryPolicy.LeaseDuration > production);
    }

    [Fact]
    public void TheCodeDefault_IsUnchanged_AndTooShortForTheJournalAskWorstCase()
    {
        // Documents why Production must set the value explicitly; Nutrition, Weekly Review and the Action
        // Agent keep the same shared default.
        var options = NutritionAiConfiguration.Read(new ConfigurationBuilder().Build());

        Assert.Equal(TimeSpan.FromSeconds(60), options.Timeout);
        Assert.True(options.Timeout < OpenAIAnswerWorstCase);
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));
}
