namespace LifeOS.Application.Users.GetCurrentUser;

public sealed class GetCurrentUserHandler
{
    private readonly IUserRepository _userRepository;

    public GetCurrentUserHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    // Null when the user no longer exists.
    public async Task<CurrentUser?> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);

        return user is null
            ? null
            : CurrentUser.From(user);
    }
}
