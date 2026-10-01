using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;

namespace LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;

// Monthly analytics for one user: two user-scoped queries (the range's transactions, the user's
// categories), then MonthlyAnalyticsCalculator in memory. Read-only, no caching.
public sealed class GetMonthlyAnalyticsHandler
{
    // A local calendar month is at most 31 days plus a daylight-saving hour; the cap also keeps the
    // query bounded (never all history).
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(32);

    private readonly ITransactionRepository _transactionRepository;
    private readonly ICategoryRepository _categoryRepository;

    public GetMonthlyAnalyticsHandler(ITransactionRepository transactionRepository, ICategoryRepository categoryRepository)
    {
        _transactionRepository = transactionRepository;
        _categoryRepository = categoryRepository;
    }

    public async Task<GetMonthlyAnalyticsResult> HandleAsync(
        Guid userId,
        GetMonthlyAnalyticsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.FromUtc.Offset != TimeSpan.Zero)
        {
            return GetMonthlyAnalyticsResult.Invalid("fromUtc", "fromUtc must be a UTC value.");
        }

        if (query.ToUtc.Offset != TimeSpan.Zero)
        {
            return GetMonthlyAnalyticsResult.Invalid("toUtc", "toUtc must be a UTC value.");
        }

        if (query.FromUtc >= query.ToUtc)
        {
            return GetMonthlyAnalyticsResult.Invalid("toUtc", "toUtc must be later than fromUtc.");
        }

        if (query.ToUtc - query.FromUtc > MaxRange)
        {
            return GetMonthlyAnalyticsResult.Invalid("toUtc", "The range must be at most 32 days (one month).");
        }

        var transactions = await _transactionRepository.GetByOccurredRangeAsync(
            userId,
            query.FromUtc,
            query.ToUtc,
            cancellationToken);

        var categories = await _categoryRepository.GetAllAsync(userId, cancellationToken);

        return GetMonthlyAnalyticsResult.Ok(MonthlyAnalyticsCalculator.Build(transactions, categories));
    }
}
