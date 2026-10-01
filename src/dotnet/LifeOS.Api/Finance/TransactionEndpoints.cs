using System.Globalization;
using System.Text.RegularExpressions;
using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Finance.Transactions.DeleteTransaction;
using LifeOS.Application.Finance.Transactions.GetRecentTransactions;
using LifeOS.Application.Finance.Transactions.GetTransaction;
using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Application.Finance.Transactions.UpdateTransaction;
using LifeOS.Contracts.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Finance;

public static partial class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Transactions are owned by the authenticated user; the user id comes only from the access token.
        var transactions = endpoints.MapGroup("/api/transactions")
            .RequireAuthorization();

        transactions.MapPost("/", CreateTransactionAsync)
            .WithName("CreateTransaction");

        transactions.MapGet("/", GetTransactionsAsync)
            .WithName("GetTransactions");

        transactions.MapGet("/recent", GetRecentTransactionsAsync)
            .WithName("GetRecentTransactions");

        // Transaction management: detail, edit (type immutable) and delete.
        transactions.MapGet("/{transactionId:guid}", GetTransactionAsync)
            .WithName("GetTransaction");

        transactions.MapPut("/{transactionId:guid}", UpdateTransactionAsync)
            .WithName("UpdateTransaction");

        transactions.MapDelete("/{transactionId:guid}", DeleteTransactionAsync)
            .WithName("DeleteTransaction");

        return endpoints;
    }

    // The caller's newest transactions (default 5, at most 20). limit is parsed here so malformed
    // input returns a ValidationProblem.
    public static async Task<Results<Ok<IReadOnlyList<TransactionResponse>>, ValidationProblem>> GetRecentTransactionsAsync(
        string? limit,
        AuthenticatedUser user,
        GetRecentTransactionsHandler handler,
        CancellationToken cancellationToken)
    {
        var count = GetRecentTransactionsHandler.DefaultLimit;

        if (limit is not null && !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out count))
        {
            return ValidationError("limit", $"limit must be a whole number between 1 and {GetRecentTransactionsHandler.MaxLimit}.");
        }

        var result = await handler.HandleAsync(user.UserId, new GetRecentTransactionsQuery(count), cancellationToken);

        if (result.Status == GetRecentTransactionsStatus.Invalid)
        {
            return ValidationError(result.Field!, result.Message!);
        }

        return TypedResults.Ok(ToResponses(result.Transactions));
    }

    // Query values are parsed here (not by model binding) so malformed input returns a ValidationProblem.
    public static async Task<Results<Ok<IReadOnlyList<TransactionResponse>>, ValidationProblem>> GetTransactionsAsync(
        string? fromUtc,
        string? toUtc,
        AuthenticatedUser user,
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

        var result = await handler.HandleAsync(user.UserId, new GetTransactionsQuery(from, to), cancellationToken);

        if (result.Status == GetTransactionsStatus.Invalid)
        {
            return ValidationError(result.Field!, result.Message!);
        }

        return TypedResults.Ok(ToResponses(result.Transactions));
    }

    // 200 with the transaction, 404 for a missing (or another user's) transaction.
    public static async Task<Results<Ok<TransactionResponse>, ProblemHttpResult>> GetTransactionAsync(
        Guid transactionId,
        AuthenticatedUser user,
        GetTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        var transaction = await handler.HandleAsync(user.UserId, transactionId, cancellationToken);

        return transaction is null ? TransactionNotFound() : TypedResults.Ok(ToResponse(transaction));
    }

    // 200 with the updated transaction; 400 for an invalid request (including a branch that does not
    // match the stored type); 404 for a missing transaction or a missing referenced account/category.
    public static async Task<Results<Ok<TransactionResponse>, ValidationProblem, ProblemHttpResult>> UpdateTransactionAsync(
        Guid transactionId,
        UpdateTransactionRequest request,
        AuthenticatedUser user,
        UpdateTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.OccurredAtUtc is not { } occurredAtUtc)
        {
            return ValidationError("occurredAtUtc", "The time the transaction occurred is required.");
        }

        UpdateTransactionResult result;

        try
        {
            result = await handler.HandleAsync(
                user.UserId,
                new UpdateTransactionCommand(
                    transactionId,
                    request.Amount,
                    occurredAtUtc,
                    request.Note,
                    request.AccountTransaction is { } account
                        ? new AccountTransactionInput(account.AccountId, account.CategoryId)
                        : null,
                    request.Transfer is { } transfer
                        ? new TransferInput(transfer.SourceAccountId, transfer.DestinationAccountId)
                        : null),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(ToUpdateFieldName(exception.ParamName), exception.Message);
        }

        return result.Status switch
        {
            UpdateTransactionStatus.Invalid => ValidationError(result.Field!, result.Message!),

            UpdateTransactionStatus.NotFound when result.Field is null => TransactionNotFound(),

            UpdateTransactionStatus.NotFound => TypedResults.Problem(
                title: "Referenced resource not found.",
                detail: result.Message,
                statusCode: StatusCodes.Status404NotFound),

            _ => TypedResults.Ok(ToResponse(result.Transaction!))
        };
    }

    // 204 when deleted, 404 for a missing (or another user's) transaction.
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteTransactionAsync(
        Guid transactionId,
        AuthenticatedUser user,
        DeleteTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, transactionId, cancellationToken);

        return result == DeleteTransactionResult.Deleted ? TypedResults.NoContent() : TransactionNotFound();
    }

    private static ProblemHttpResult TransactionNotFound() =>
        TypedResults.Problem(
            title: "Transaction not found.",
            detail: UpdateTransactionResult.TransactionNotFoundMessage,
            statusCode: StatusCodes.Status404NotFound);

    // Domain parameter names → the nested request fields.
    private static string ToUpdateFieldName(string? parameterName) => parameterName switch
    {
        "accountId" => "accountTransaction.accountId",
        "categoryId" => "accountTransaction.categoryId",
        "sourceAccountId" => "transfer.sourceAccountId",
        "destinationAccountId" => "transfer.destinationAccountId",
        null => "request",
        _ => parameterName
    };

    private static TransactionResponse ToResponse(TransactionSummary transaction) => ToResponses([transaction])[0];

    private static IReadOnlyList<TransactionResponse> ToResponses(IEnumerable<TransactionSummary> transactions) =>
        transactions
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

    // Requires an explicit offset ("Z" or "+hh:mm"); offset-less values are never assumed to be UTC.
    // Whether the offset is zero is checked by the Application layer.
    internal static string? TryParseUtcInstant(string name, string? value, out DateTimeOffset instant)
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
        AuthenticatedUser user,
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
                user.UserId,
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
