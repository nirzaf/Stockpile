using System.Globalization;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Allocates scoped document numbers inside the caller's unit-of-work transaction.</summary>
public sealed class DocumentNumberService(InventoryDbContext context, IUnitOfWork unitOfWork) : IDocumentNumberService
{
    public async Task<string> AllocateAsync(int companyId, string documentType, int period, string prefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (companyId <= 0) throw new ArgumentOutOfRangeException(nameof(companyId));
        if (period is < 2000 or > 9999) throw new ArgumentOutOfRangeException(nameof(period));

        string? allocated = null;
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            if (context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // Row locks cannot protect the not-yet-created sequence row. A transaction-scoped
                // advisory lock serializes the initial insert and all later allocations for this
                // tenant/company/type/period tuple without holding a process-local lock.
                var lockKey = string.Create(CultureInfo.InvariantCulture,
                    $"{context.CurrentTenantId}\u001f{companyId}\u001f{documentType}\u001f{period}");
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                    cancellationToken);
            }

            var sequence = await context.DocumentNumberSequences
                .SingleOrDefaultAsync(item => item.CompanyId == companyId &&
                    item.DocumentType == documentType && item.Period == period, cancellationToken);
            if (sequence is null)
            {
                sequence = new DocumentNumberSequence
                {
                    CompanyId = companyId, DocumentType = documentType, Period = period, Prefix = prefix
                };
                context.DocumentNumberSequences.Add(sequence);
            }

            var number = sequence.NextNumber++;
            allocated = $"{sequence.Prefix}{period:0000}-{number:000000}";
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return allocated!;
    }
}
