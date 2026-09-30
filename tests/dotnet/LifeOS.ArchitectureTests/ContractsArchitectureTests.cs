using System.Reflection;
using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.ArchitectureTests;

// ADR-006: the owning user is derived only from the authenticated identity. No request contract
// may let a client supply a user id. Response contracts (e.g. MeResponse) may expose it.
public class ContractsArchitectureTests
{
    [Fact]
    public void Request_Contracts_Should_Not_Expose_UserId()
    {
        var requestTypes = typeof(CreateAccountRequest).Assembly
            .GetExportedTypes()
            .Where(type => type.Name.EndsWith("Request", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var offenders = requestTypes
            .Where(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => string.Equals(property.Name, "UserId", StringComparison.OrdinalIgnoreCase)))
            .Select(type => type.FullName)
            .ToList();

        Assert.NotEmpty(requestTypes);
        Assert.True(offenders.Count == 0, "Request contracts exposing UserId: " + string.Join(", ", offenders));
    }
}
