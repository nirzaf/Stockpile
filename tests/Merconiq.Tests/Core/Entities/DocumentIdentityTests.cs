using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Core.Entities;

public sealed class DocumentIdentityTests
{
    [Fact]
    public async Task Numbered_document_normalizes_its_type_and_prefix_before_allocating()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"document-identity-{Guid.NewGuid():N}")
            .Options;
        await using var context = new InventoryDbContext(options, new TestTenantContext("identity-tenant"));
        var company = new Company { Code = $"DI-{Guid.NewGuid():N}"[..15], LegalName = "Identity test company" };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var service = new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork));
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("invoice request")));
        var identity = await service.CreateNumberedAsync(
            company.Id,
            " Invoice ",
            2026,
            " INV- ",
            "Invoice.Create",
            "request-1",
            requestHash);

        identity.DocumentType.Should().Be("Invoice");
        identity.HumanNumber.Should().Be("INV-2026-000001");
        (await context.DocumentNumberSequences.SingleAsync()).Prefix.Should().Be("INV-");
    }

    [Fact]
    public void Create_rejects_a_default_identity()
    {
        var act = () => DocumentIdentity.Create(
            default,
            "tenant-a",
            null,
            "PurchaseOrder",
            "PO-001",
            2026,
            DocumentLifecycleStatus.Draft,
            "PurchaseOrder.Create");

        act.Should().Throw<ArgumentException>().WithMessage("A document identity cannot be empty.*");
    }

    [Theory]
    [InlineData(DocumentLifecycleStatus.Cancelled)]
    [InlineData(DocumentLifecycleStatus.Voided)]
    public void Terminal_transition_retains_identity_and_cannot_be_reactivated(DocumentLifecycleStatus terminal)
    {
        var id = DocumentIdentityId.New();
        var identity = DocumentIdentity.Create(
            id,
            "tenant-a",
            null,
            "PurchaseOrder",
            "PO-001",
            2026,
            DocumentLifecycleStatus.Draft,
            "PurchaseOrder.Create");

        identity.TransitionTo(terminal);

        identity.Id.Should().Be(id);
        identity.HumanNumber.Should().Be("PO-001");
        identity.Status.Should().Be(terminal);
        var reactivate = () => identity.TransitionTo(DocumentLifecycleStatus.Active);
        reactivate.Should().Throw<InvalidOperationException>()
            .WithMessage($"Invalid document lifecycle transition: {terminal} -> Active.");
    }
}
