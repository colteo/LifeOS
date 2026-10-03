using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Application.Finance.Accounts.SetOpeningBalance;
using LifeOS.Application.Finance.Accounts.UpdateAccount;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Finance;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Accounts are owned by the authenticated user; the user id comes only from the access token.
        var accounts = endpoints.MapGroup("/api/accounts")
            .RequireAuthorization();

        accounts.MapPost("/", CreateAccountAsync)
            .WithName("CreateAccount");

        accounts.MapGet("/", GetAccountsAsync)
            .WithName("GetAccounts");

        // Derived balances and opening balances (ADR-007).
        accounts.MapGet("/balances", GetAccountBalancesAsync)
            .WithName("GetAccountBalances");

        accounts.MapPut("/{accountId:guid}/opening-balance", SetOpeningBalanceAsync)
            .WithName("SetOpeningBalance");

        // Account management: name and type are editable, the currency is not.
        accounts.MapPut("/{accountId:guid}", UpdateAccountAsync)
            .WithName("UpdateAccount");

        accounts.MapDelete("/{accountId:guid}", DeleteAccountAsync)
            .WithName("DeleteAccount");

        return endpoints;
    }

    public static async Task<Results<Created<AccountResponse>, ValidationProblem>> CreateAccountAsync(
        CreateAccountRequest request,
        AuthenticatedUser user,
        CreateAccountHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseAccountType(request.Type, out var accountType))
        {
            return ValidationError("type", "Account type is not supported.");
        }

        OpeningBalanceInput? openingBalance = null;

        if (request.OpeningBalance is { } requested)
        {
            if (requested.AsOfUtc is not { } asOfUtc)
            {
                return ValidationError("openingBalance.asOfUtc", "The instant of the balance is required.");
            }

            openingBalance = new OpeningBalanceInput(requested.Amount, asOfUtc);
        }

        CreateAccountResult result;

        try
        {
            result = await handler.HandleAsync(
                user.UserId,
                new CreateAccountCommand(request.Name, accountType, request.Currency, openingBalance),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(ToFieldName(exception.ParamName), exception.Message);
        }

        var response = new AccountResponse(
            result.Id,
            result.Name,
            result.AccountType.ToString(),
            result.Currency,
            result.CreatedAtUtc);

        return TypedResults.Created($"/api/accounts/{response.Id}", response);
    }

    public static async Task<Ok<IReadOnlyList<AccountResponse>>> GetAccountsAsync(
        AuthenticatedUser user,
        GetAccountsHandler handler,
        CancellationToken cancellationToken)
    {
        var accounts = await handler.HandleAsync(user.UserId, cancellationToken);

        IReadOnlyList<AccountResponse> response = accounts
            .Select(account => new AccountResponse(
                account.Id,
                account.Name,
                account.AccountType.ToString(),
                account.Currency,
                account.CreatedAtUtc))
            .ToList();

        return TypedResults.Ok(response);
    }

    // Query values are parsed here so malformed input returns a ValidationProblem. atUtc is optional
    // (default: now) and, when given, must state UTC explicitly.
    public static async Task<Results<Ok<IReadOnlyList<AccountBalanceResponse>>, ValidationProblem>> GetAccountBalancesAsync(
        string? atUtc,
        AuthenticatedUser user,
        GetAccountBalancesHandler handler,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? at = null;

        if (atUtc is not null)
        {
            if (TransactionEndpoints.TryParseUtcInstant("atUtc", atUtc, out var parsed) is { } error)
            {
                return ValidationError("atUtc", error);
            }

            at = parsed;
        }

        var result = await handler.HandleAsync(user.UserId, new GetAccountBalancesQuery(at), cancellationToken);

        if (result.Status == GetAccountBalancesStatus.Invalid)
        {
            return ValidationError(result.Field!, result.Message!);
        }

        IReadOnlyList<AccountBalanceResponse> response = result.Balances
            .Select(balance => new AccountBalanceResponse(
                balance.AccountId,
                balance.Currency,
                balance.Balance,
                balance.AtUtc,
                balance.OpeningBalance is null ? null : ToResponse(balance.OpeningBalance)))
            .ToList();

        return TypedResults.Ok(response);
    }

    // Create-only in v1: 201 when created, 200 when the same values already exist, 409 when
    // different values exist, 404 for a missing (or another user's) account.
    public static async Task<Results<Created<OpeningBalanceResponse>, Ok<OpeningBalanceResponse>, ValidationProblem, ProblemHttpResult>> SetOpeningBalanceAsync(
        Guid accountId,
        OpeningBalanceRequest request,
        AuthenticatedUser user,
        SetOpeningBalanceHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.AsOfUtc is not { } asOfUtc)
        {
            return ValidationError("asOfUtc", "The instant of the balance is required.");
        }

        SetOpeningBalanceResult result;

        try
        {
            result = await handler.HandleAsync(
                user.UserId,
                new SetOpeningBalanceCommand(accountId, request.Amount, asOfUtc),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(exception.ParamName == "asOfUtc" ? "asOfUtc" : "amount", exception.Message);
        }

        return result.Status switch
        {
            SetOpeningBalanceStatus.Created => TypedResults.Created(
                $"/api/accounts/{accountId}/opening-balance",
                ToResponse(result.OpeningBalance!)),

            SetOpeningBalanceStatus.Unchanged => TypedResults.Ok(ToResponse(result.OpeningBalance!)),

            SetOpeningBalanceStatus.NotFound => TypedResults.Problem(
                title: "Account not found.",
                detail: $"Account '{accountId}' does not exist.",
                statusCode: StatusCodes.Status404NotFound),

            _ => TypedResults.Problem(
                title: "Opening balance already set.",
                detail: "This account already has a different opening balance; changing it is not supported.",
                statusCode: StatusCodes.Status409Conflict)
        };
    }

    // 200 with the updated account, 404 for a missing (or another user's) account.
    public static async Task<Results<Ok<AccountResponse>, ValidationProblem, ProblemHttpResult>> UpdateAccountAsync(
        Guid accountId,
        UpdateAccountRequest request,
        AuthenticatedUser user,
        UpdateAccountHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseAccountType(request.Type, out var accountType))
        {
            return ValidationError("type", "Account type is not supported.");
        }

        UpdateAccountResult result;

        try
        {
            result = await handler.HandleAsync(
                user.UserId,
                new UpdateAccountCommand(accountId, request.Name, accountType),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(ToFieldName(exception.ParamName), exception.Message);
        }

        if (result.Status == UpdateAccountStatus.NotFound)
        {
            return AccountNotFound(accountId);
        }

        var account = result.Account!;

        return TypedResults.Ok(new AccountResponse(
            account.Id,
            account.Name,
            account.AccountType.ToString(),
            account.Currency,
            account.CreatedAtUtc));
    }

    // 204 when deleted (with its opening balance), 404 for a missing (or another user's) account,
    // 409 when transactions reference it or it changed while being deleted.
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAccountAsync(
        Guid accountId,
        AuthenticatedUser user,
        DeleteAccountHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, accountId, cancellationToken);

        return result switch
        {
            DeleteAccountResult.Deleted => TypedResults.NoContent(),

            DeleteAccountResult.HasPlannedExpenses => TypedResults.Problem(statusCode: 409,
                title: "Referenced by planned expenses", detail: "Delete the referencing planned expenses first."),
            DeleteAccountResult.HasRecurringRules => TypedResults.Problem(statusCode: 409,
                title: "Account has recurring rules", detail: "Delete the recurring rules referencing this account first."),
            DeleteAccountResult.HasTransactions => TypedResults.Problem(
                title: "Account in use.",
                detail: "This account can't be deleted because it has transactions.",
                statusCode: StatusCodes.Status409Conflict),

            DeleteAccountResult.HasReconciliations => TypedResults.Problem(
                title: "Account in use.",
                detail: "This account can't be deleted because it has reconciliation history.",
                statusCode: StatusCodes.Status409Conflict),

            DeleteAccountResult.Changed => TypedResults.Problem(
                title: "Account changed.",
                detail: "The account changed while it was being deleted. Please try again.",
                statusCode: StatusCodes.Status409Conflict),

            _ => AccountNotFound(accountId)
        };
    }

    private static ProblemHttpResult AccountNotFound(Guid accountId) =>
        TypedResults.Problem(
            title: "Account not found.",
            detail: $"Account '{accountId}' does not exist.",
            statusCode: StatusCodes.Status404NotFound);

    private static OpeningBalanceResponse ToResponse(OpeningBalanceSummary openingBalance) =>
        new(openingBalance.AccountId, openingBalance.Amount, openingBalance.AsOfUtc);

    private static bool TryParseAccountType(string? value, out AccountType accountType)
    {
        // Match names only: Enum.TryParse would also accept numeric and comma-combined values.
        var name = Enum.GetNames<AccountType>()
            .FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));

        accountType = name is null ? default : Enum.Parse<AccountType>(name);

        return name is not null;
    }

    private static string ToFieldName(string? parameterName) => parameterName switch
    {
        "accountType" => "type",
        "amount" => "openingBalance.amount",
        "asOfUtc" => "openingBalance.asOfUtc",
        null => "request",
        _ => parameterName
    };

    private static ValidationProblem ValidationError(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });
}
