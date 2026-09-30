using LifeOS.Domain.Users;

namespace LifeOS.Application.Users.SignInWithExternalIdentity;

// Resolves or creates the LifeOS user for an external identity. The first sign-in registers the user.
// Proof of the identity (e.g. Google) is the caller's responsibility; nothing is verified here.
// Users are matched only by (provider, subject), never by email.
public sealed class SignInWithExternalIdentityHandler
{
    private readonly IUserRepository _userRepository;
    private readonly TimeProvider _timeProvider;

    public SignInWithExternalIdentityHandler(IUserRepository userRepository, TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _timeProvider = timeProvider;
    }

    public async Task<SignInWithExternalIdentityResult> HandleAsync(
        SignInWithExternalIdentityCommand command,
        CancellationToken cancellationToken)
    {
        var provider = ExternalIdentity.NormalizeProvider(command.Provider);
        var subject = ExternalIdentity.NormalizeSubject(command.Subject);
        var now = _timeProvider.GetUtcNow();

        var identity = await _userRepository.GetExternalIdentityAsync(provider, subject, cancellationToken);

        if (identity is not null)
        {
            // The user profile (display name, email) is not synchronized on later sign-ins.
            identity.RecordSignIn(command.Email, now);

            await _userRepository.UpdateExternalIdentityAsync(identity, cancellationToken);

            return new SignInWithExternalIdentityResult(identity.UserId, IsNewUser: false);
        }

        var user = User.CreateFromExternalIdentity(command.DisplayName, command.Email, now);
        var newIdentity = ExternalIdentity.Create(user.Id, provider, subject, command.Email, now);

        if (await _userRepository.TryAddAsync(user, newIdentity, cancellationToken))
        {
            return new SignInWithExternalIdentityResult(user.Id, IsNewUser: true);
        }

        // A concurrent first sign-in created the same identity between the lookup and the save.
        // Nothing of ours was persisted; resolve to the user that won, reading once only.
        var existing = await _userRepository.GetExternalIdentityAsync(provider, subject, cancellationToken)
            ?? throw new InvalidOperationException(
                "The external identity already exists but could not be read back.");

        return new SignInWithExternalIdentityResult(existing.UserId, IsNewUser: false);
    }
}
