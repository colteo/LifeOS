using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Contracts.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Finance;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var transactions = endpoints.MapGroup("/api/transactions");

        transactions.MapPost("/", CreateTransactionAsync)
            .WithName("CreateTransaction");

        return endpoints;
    }

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
