using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Domain.Users;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Users;

public class GetCurrentUserHandlerTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUserRepository _repository = new();

    [Fact]
    public async Task HandleAsync_WithExistingUser_ReturnsProfile()
    {
        var user = User.CreateFromExternalIdentity("Test Person", "person@example.com", CreatedAtUtc);
        _repository.Users.Add(user);

        var result = await new GetCurrentUserHandler(_repository).HandleAsync(user.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal("Test Person", result.DisplayName);
        Assert.Equal("person@example.com", result.Email);
        Assert.Equal(OnboardingStatus.PendingFinanceProfile, result.OnboardingStatus);
        Assert.Null(result.DefaultCurrency);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownUser_ReturnsNull()
    {
        _repository.Users.Add(User.CreateFromExternalIdentity(null, null, CreatedAtUtc));

        var result = await new GetCurrentUserHandler(_repository).HandleAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(result);
    }
}
