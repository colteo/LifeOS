namespace LifeOS.Application.Users.SignInWithExternalIdentity;

// A provider-neutral identity that the outer boundary has already verified.
public sealed record SignInWithExternalIdentityCommand(
    string Provider,
    string Subject,
    string? Email,
    string? DisplayName);
