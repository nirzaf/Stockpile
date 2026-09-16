namespace Merconiq.Core.Interfaces;

public interface IDocumentNumberService
{
    Task<string> AllocateAsync(int companyId, string documentType, int period, string prefix,
        CancellationToken cancellationToken = default);
}
