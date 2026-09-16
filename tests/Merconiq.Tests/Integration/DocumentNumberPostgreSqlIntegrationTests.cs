using FluentAssertions;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class DocumentNumberPostgreSqlIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _fixture;

    public DocumentNumberPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [PostgreSqlFact]
    public async Task Concurrent_first_document_number_allocations_are_unique_and_contiguous()
    {
        _fixture.EnsureEnabled();
        const int allocationCount = 32;
        var tenantId = $"document-number-race-{Guid.NewGuid():N}";
        var documentType = $"INVOICE-{Guid.NewGuid():N}";
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pendingAllocations = Enumerable.Range(0, allocationCount)
            .Select(async _ =>
            {
                await startGate.Task;
                await using var context = _fixture.CreateContext(tenantId);
                var service = new DocumentNumberService(context, new UnitOfWork(context));
                return await service.AllocateAsync(42, documentType, 2026, "INV-");
            })
            .ToArray();

        startGate.SetResult(true);
        var numbers = await Task.WhenAll(pendingAllocations);

        numbers.Should().OnlyHaveUniqueItems();
        numbers
            .Select(number => int.Parse(number[(number.LastIndexOf('-') + 1)..]))
            .Order()
            .Should()
            .Equal(Enumerable.Range(1, allocationCount));

        await using var verify = _fixture.CreateContext(tenantId);
        var sequence = await verify.DocumentNumberSequences
            .SingleAsync(item => item.CompanyId == 42 && item.DocumentType == documentType && item.Period == 2026);
        sequence.NextNumber.Should().Be(allocationCount + 1);
    }
}
