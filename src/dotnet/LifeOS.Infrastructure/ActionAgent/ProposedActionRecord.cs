using System.Text.Json;
using LifeOS.Domain.ActionAgent;

namespace LifeOS.Infrastructure.ActionAgent;

// The proposed_actions row (AI-002). A persistence record rather than the Domain entity: the payload is a
// versioned jsonb document read according to payload_version (like the weekly review snapshot, AUTO-002
// D-3/D-4). The payload columns are written once by the insert; afterwards only the status columns move.
internal sealed class ProposedActionRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ReviewId { get; set; }

    public string ActionType { get; set; } = "";

    public string Status { get; set; } = "";

    public int PayloadVersion { get; set; }

    // jsonb document; see ProposedActionPayloadJson.
    public string Payload { get; set; } = "";

    public string Rationale { get; set; } = "";

    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    public string PromptVersion { get; set; } = "";

    public string ToolSchemaVersion { get; set; } = "";

    public string[] ToolCalls { get; set; } = [];

    public int StepCount { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }

    public DateTimeOffset? ExecutedAtUtc { get; set; }

    public string? FailureCode { get; set; }

    public static ProposedActionRecord From(ProposedAction proposal) => new()
    {
        Id = proposal.Id,
        UserId = proposal.UserId,
        ReviewId = proposal.ReviewId,
        ActionType = proposal.ActionType.ToString(),
        Status = proposal.Status.ToString(),
        PayloadVersion = proposal.PayloadVersion,
        Payload = ProposedActionPayloadJson.Serialize(proposal.PayloadVersion, proposal.Payload),
        Rationale = proposal.Rationale,
        Provider = proposal.Run.Provider,
        Model = proposal.Run.Model,
        PromptVersion = proposal.Run.PromptVersion,
        ToolSchemaVersion = proposal.Run.ToolSchemaVersion,
        ToolCalls = [.. proposal.Run.ToolCalls],
        StepCount = proposal.Run.StepCount,
        CreatedAtUtc = proposal.CreatedAtUtc,
        DecidedAtUtc = proposal.DecidedAtUtc,
        ExecutedAtUtc = proposal.ExecutedAtUtc,
        FailureCode = proposal.FailureCode
    };

    public ProposedAction ToDomain() => ProposedAction.Restore(
        Id,
        UserId,
        ReviewId,
        Enum.Parse<ProposedActionType>(ActionType),
        PayloadVersion,
        ProposedActionPayloadJson.Deserialize(PayloadVersion, Payload),
        Rationale,
        new AgentRunIdentity(Provider, Model, PromptVersion, ToolSchemaVersion, ToolCalls, StepCount),
        Enum.Parse<ProposedActionStatus>(Status),
        CreatedAtUtc,
        DecidedAtUtc,
        ExecutedAtUtc,
        FailureCode);
}

// Fixed options: camelCase names in declaration order, no indentation. The stored payload_version
// selects the reader; an unknown version is an error, never a guess.
internal static class ProposedActionPayloadJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string Serialize(int payloadVersion, MonthlyBudgetAdjustment payload) => payloadVersion switch
    {
        1 => JsonSerializer.Serialize(payload, Options),
        _ => throw new NotSupportedException($"Proposed action payload version {payloadVersion} cannot be written.")
    };

    public static MonthlyBudgetAdjustment Deserialize(int payloadVersion, string document) => payloadVersion switch
    {
        1 => JsonSerializer.Deserialize<MonthlyBudgetAdjustment>(document, Options)
            ?? throw new InvalidOperationException("The proposed action payload is empty."),
        _ => throw new NotSupportedException($"Proposed action payload version {payloadVersion} is not supported.")
    };
}
