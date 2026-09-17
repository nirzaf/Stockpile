using Merconiq.Core.Interfaces;

namespace Merconiq.Core.Services;

public static class UnitOfWorkExtensions
{
    public static Task ExecuteMasterDataWriteAsync(
        this IUnitOfWork unitOfWork,
        Func<Task> operation,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await unitOfWork.AcquireTenantOperationLockAsync("master-data-import", cancellationToken);
            await operation();
        }, cancellationToken);
}
