namespace LifeOS.Domain.Users;

// A LifeOS sign-in session, represented to the client by an opaque refresh token.
// Only the token's hash is stored. Each refresh rotates the session: the current row is
// revoked and replaced by a new row in the same family (the chain started by one sign-in).
public sealed class UserSession
{
    private UserSession(
        Guid id,
        Guid userId,
        Guid familyId,
        string refreshTokenHash,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? revokedAtUtc,
        Guid? replacedBySessionId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        FamilyId = familyId;
        RefreshTokenHash = refreshTokenHash;
        ExpiresAtUtc = expiresAtUtc;
        RevokedAtUtc = revokedAtUtc;
        ReplacedBySessionId = replacedBySessionId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    // The Id of the first session of the chain; shared by every rotation.
    public Guid FamilyId { get; }

    public string RefreshTokenHash { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    // Set when the session was rotated. Presenting a rotated session's token again is token reuse.
    public Guid? ReplacedBySessionId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public bool IsRotated => ReplacedBySessionId is not null;

    public static UserSession Start(
        Guid userId,
        string refreshTokenHash,
        DateTimeOffset createdAtUtc,
        TimeSpan lifetime)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        var id = Guid.CreateVersion7();

        return Create(id, userId, familyId: id, refreshTokenHash, createdAtUtc, lifetime);
    }

    public bool IsActive(DateTimeOffset nowUtc) => RevokedAtUtc is null && nowUtc < ExpiresAtUtc;

    // Revokes this session and returns its replacement in the same family, with a fresh (sliding) expiry.
    public UserSession RotateTo(string refreshTokenHash, DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        if (!IsActive(nowUtc))
        {
            throw new InvalidOperationException("Only an active session can be rotated.");
        }

        var replacement = Create(Guid.CreateVersion7(), UserId, FamilyId, refreshTokenHash, nowUtc, lifetime);

        RevokedAtUtc = nowUtc.ToUniversalTime();
        ReplacedBySessionId = replacement.Id;

        return replacement;
    }

    private static UserSession Create(
        Guid id,
        Guid userId,
        Guid familyId,
        string refreshTokenHash,
        DateTimeOffset createdAtUtc,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshTokenHash);

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Session lifetime must be positive.");
        }

        var createdAt = createdAtUtc.ToUniversalTime();

        return new UserSession(
            id,
            userId,
            familyId,
            refreshTokenHash,
            createdAt + lifetime,
            revokedAtUtc: null,
            replacedBySessionId: null,
            createdAt);
    }
}
