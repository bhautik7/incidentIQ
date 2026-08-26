using Microsoft.EntityFrameworkCore;

namespace IncidentIQ.Persistence;

/// <summary>
/// Runs a unit of work inside one database transaction.
///
/// <b>Why this exists rather than calling BeginTransactionAsync directly.</b>
/// The context is configured with <c>EnableRetryOnFailure</c>, and EF refuses
/// user-initiated transactions under a retrying execution strategy - it throws
/// rather than silently retrying only part of a transaction. Since the
/// transactional outbox is *built* on user-initiated transactions, every such
/// caller has to go through the execution strategy, and forgetting is a runtime
/// failure rather than a compile error.
///
/// Wrapping it once here means callers get the correct behaviour by default and
/// the retry policy stays in place for the transient faults it was added for.
/// </summary>
public static class TransactionExtensions
{
    /// <summary>
    /// Executes <paramref name="work"/>, saves, and commits - retrying the
    /// whole unit on a transient fault.
    ///
    /// <b>The delegate may run more than once.</b> On a transient failure the
    /// transaction is rolled back and the entire block re-executed, so
    /// <paramref name="work"/> must be safe to repeat: stage entities and
    /// compute values inside it, and do not perform side effects outside the
    /// database from within it.
    /// </summary>
    public static async Task ExecuteInTransactionAsync(
        this IncidentIQDbContext dbContext,
        Func<Task> work,
        CancellationToken cancellationToken = default)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await work();
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>Result-returning overload, for a caller that needs the row it just created.</summary>
    public static async Task<TResult> ExecuteInTransactionAsync<TResult>(
        this IncidentIQDbContext dbContext,
        Func<Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var result = await work();
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return result;
        });
    }
}
