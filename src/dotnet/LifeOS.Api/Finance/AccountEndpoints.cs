using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Finance;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var accounts = endpoints.MapGroup("/api/accounts");

        accounts.MapPost("/", CreateAccountAsync)
            .WithName("CreateAccount");

        return endpoints;
    }

    public static async Task<Results<Created<AccountResponse>, ValidationProblem>> CreateAccountAsync(
        CreateAccountRequest request,
        CreateAccountHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseAccountType(request.Type, out var accountType))
        {
            return ValidationError("type", "Account type is not supported.");
        }

        CreateAccountResult result;

        try
        {
            result = await handler.HandleAsync(
                new CreateAccountCommand(request.Name, accountType, request.Currency),
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
        null => "request",
        _ => parameterName
    };

    private static ValidationProblem ValidationError(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });
}
