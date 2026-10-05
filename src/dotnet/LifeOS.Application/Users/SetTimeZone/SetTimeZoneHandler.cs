using LifeOS.Domain.Users;

namespace LifeOS.Application.Users.SetTimeZone;

public enum SetTimeZoneResult
{
    Updated,
    Unchanged,
    Invalid,
    NotFound
}

public sealed class SetTimeZoneHandler
{
    private readonly IUserRepository _users;

    public SetTimeZoneHandler(IUserRepository users)
    {
        _users = users;
    }

    public async Task<SetTimeZoneResult> HandleAsync(
        Guid userId,
        string? timeZoneId,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeIanaTimeZone(timeZoneId, out var normalized))
        {
            return SetTimeZoneResult.Invalid;
        }

        var user = await _users.GetByIdAsync(userId, cancellationToken);

        if (user is null)
        {
            return SetTimeZoneResult.NotFound;
        }

        if (string.Equals(user.TimeZoneId, normalized, StringComparison.Ordinal))
        {
            return SetTimeZoneResult.Unchanged;
        }

        user.SetTimeZone(normalized);

        return await _users.UpdateTimeZoneAsync(user.Id, user.TimeZoneId!, cancellationToken)
            ? SetTimeZoneResult.Updated
            : SetTimeZoneResult.NotFound;
    }

    public static bool TryNormalizeIanaTimeZone(string? timeZoneId, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        var candidate = timeZoneId.Trim();

        if (candidate.Length > User.MaxTimeZoneIdLength)
        {
            return false;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(candidate);

            if (!zone.HasIanaId)
            {
                return false;
            }

            normalized = zone.Id;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
