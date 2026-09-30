using System.Globalization;
using System.Text.RegularExpressions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Contracts.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Finance;

public static partial class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var transactions = endpoints.MapGroup("/api/transactions");

        transactions.MapPost("/", CreateTransactionAsync)
            .WithName("CreateTransaction");

        transactions.MapGet("/", GetTransactionsAsync)
            .WithName("GetTransactions");

        return endpoints;
    }

    // Query values are parsed here (not by model binding) so malformed input returns a ValidationProblem.
    public static async Task<Results<Ok<IReadOnlyList<TransactionResponse>>, ValidationProblem>> GetTransactionsAsync(
        string? fromUtc,
        string? toUtc,
        GetTransactionsHandler handler,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (TryParseUtcInstant("fromUtc", fromUtc, out var from) is { } fromError)
        {
            errors["fromUtc"] = [fromError];
        }

        if (TryParseUtcInstant("toUtc", toUtc, out var to) is { } toError)
        {
            errors["toUtc"] = [toError];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await handler.HandleAsync(new GetTransactionsQuery(from, to), cancellationToken);

        if (result.Status == GetTransactionsStatus.Invalid)
        {
            return ValidationError(result.Field!, result.Message!);
        }

        IReadOnlyList<TransactionResponse> response = result.Transactions
            .Select(transaction => new TransactionResponse(
                transaction.Id,
                transaction.TransactionType.ToString(),
                transaction.Amount,
                transaction.Currency,
                transaction.AccountId,
                transaction.SourceAccountId,
                transaction.DestinationAccountId,
                transaction.CategoryId,
                transaction.OccurredAtUtc,
                transaction.Note,
                transaction.CreatedAtUtc))
            .ToList();

        return TypedResults.Ok(response);
    }

    // Requires an explicit offset ("Z" or "+hh:mm"); offset-less values are never assumed to be UTC.
    // Whether the offset is zero is checked by the Application layer.
    private static string? TryParseUtcInstant(string name, string? value, out DateTimeOffset instant)
    {
        instant = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{name} is required.";
        }

        var trimmed = value.Trim();

        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant))
        {
            return $"{name} must be an ISO 8601 date and time.";
        }

        if (!ExplicitOffsetPattern().IsMatch(trimmed))
        {
            return $"{name} must specify UTC explicitly, e.g. '2026-09-01T00:00:00Z'.";
        }

        return null;
    }

    [GeneratedRegex(@"(?:[Zz]|[+-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetPattern();

    public static async Task<Results<Created<TransactionResponse>, ValidationProblem, ProblemHttpResult>> CreateTransactionAsync(
        CreateTransactionRequest request,
        CreateTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseTransactionType(request.Type, out var transactionType))
        {
            return ValidationError("type", "Transaction type is not supported.");
        }

        if (request.OccurredAtUtc is not { } occurredAtUtc)
        {
            return ValidationError("occurredAtUtc", "The time the transaction occurred is required.");
        }

        CreateTransactionResult result;

        try
        {
            result = await handler.HandleAsync(
                new CreateTransactionCommand(
                    transactionType,
                    request.Amount,
                    request.AccountId,
                    request.SourceAccountId,
                    request.DestinationAccountId,
                    request.CategoryId,
                    occurredAtUtc,
                    request.Note),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(exception.ParamName ?? "request", exception.Message);
        }

        return result.Status switch
        {
            CreateTransactionStatus.Invalid => ValidationError(result.Field!, result.Message!),

            CreateTransactionStatus.NotFound => TypedResults.Problem(
                title: "Referenced resource not found.",
                detail: result.Message,
                statusCode: StatusCodes.Status404NotFound),

            _ => Created(result.Transaction!)
        };
    }

    private static Created<TransactionResponse> Created(CreatedTransaction transaction)
    {
        var response = new TransactionResponse(
            transaction.Id,
            transaction.TransactionType.ToString(),
            transaction.Amount,
            transaction.Currency,
            transaction.AccountId,
            transaction.SourceAccountId,
            transaction.DestinationAccountId,
            transaction.CategoryId,
            transaction.OccurredAtUtc,
            transaction.Note,
            transaction.CreatedAtUtc);

        return TypedResults.Created($"/api/transactions/{response.Id}", response);
    }

    private static bool TryParseTransactionType(string? value, out TransactionType transactionType)
    {
        // Match names only: Enum.TryParse would also accept numeric and comma-combined values.
        var name = Enum.GetNames<TransactionType>()
            .FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));

        transactionType = name is null ? default : Enum.Parse<TransactionType>(name);

        return name is not null;
    }

    private static ValidationProblem ValidationError(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });
}
