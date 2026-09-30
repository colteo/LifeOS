using LifeOS.Application.Users;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Users;

internal sealed class UserRepository : IUserRepository
{
    private readonly LifeOSDbContext _dbContext;

    public UserRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ExternalIdentity?> GetExternalIdentityAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        return await _dbContext.ExternalIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                identity => identity.Provider == provider && identity.Subject == subject,
                cancellationToken);
    }

    public async Task AddAsync(User user, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        _dbContext.Users.Add(user);
        _dbContext.ExternalIdentities.Add(identity);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        _dbContext.ExternalIdentities.Update(identity);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
