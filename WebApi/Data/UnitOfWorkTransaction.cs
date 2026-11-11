using DataAccessLayer.UnitOfWork;

namespace WebApi.Data;

public readonly record struct TransactionOutcome<T>(bool Commit, T Value);

public static class UnitOfWorkTransaction
{
    public static async Task<T> ExecuteAsync<T>(
        IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<TransactionOutcome<T>>> action,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await unitOfWork.DbContext.Database.BeginTransactionAsync(cancellationToken);
        var outcome = await action(cancellationToken);

        if (!outcome.Commit)
        {
            await transaction.RollbackAsync(cancellationToken);
            return outcome.Value;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome.Value;
    }
}
