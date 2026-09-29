using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Infrastructure.Finance.Accounts;
using LifeOS.Infrastructure.Finance.Categories;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<LifeOSDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();

        return services;
    }
}
