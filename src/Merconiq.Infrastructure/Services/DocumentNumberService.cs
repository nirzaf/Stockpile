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
