using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TransferOrderAmendmentStatePostgreSqlIntegrationTests(
    PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Amendment_does_not_overwrite_a_cancellation_committed_by_another_context()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-amend-stale-{Guid.NewGuid():N}";
        var seeded = await CreateTransferAsync(tenantId, approve: false, 2);

        await using var amendmentContext = fixture.CreateContext(tenantId);
        var amendmentService = CreateService(amendmentContext, tenantId);
        var staleOrder = await new Repository<TransferOrder>(amendmentContext)
            .GetByIdAsync(seeded.Order.Id);
        staleOrder.Should().NotBeNull();
        staleOrder!.Status.Should().Be(TransferOrderStatus.Draft);

        await using (var cancellationContext = fixture.CreateContext(tenantId))
        {
            await CreateService(cancellationContext, tenantId)
                .CancelAsync(seeded.Order.Id, new StockMutationScope(seeded.CompanyId));
        }

        await FluentAssertions.FluentActions.Invoking(() => amendmentService.AmendAsync(
                seeded.Order.Id,
                new CreateTransferOrderRequest(
                    seeded.CompanyId,
                    seeded.SourceLocationId,
                    seeded.DestinationLocationId,
                    [new TransferOrderLineRequest(seeded.ItemId, 3, LineId: seeded.Order.Lines.Single().Id)],
                    "stale amendment must not revive cancellation"),
                new StockMutationScope(seeded.CompanyId)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cancelled transfer orders cannot be amended.");

        await using var verify = fixture.CreateContext(tenantId);
        var persistedOrder = await verify.TransferOrders.SingleAsync(order => order.Id == seeded.Order.Id);
        persistedOrder.Status.Should().Be(TransferOrderStatus.Cancelled);
        persistedOrder.Notes.Should().BeNull();
        (await verify.TransferOrderLines.SingleAsync(line => line.TransferOrderId == seeded.Order.Id))
            .Quantity.Should().Be(2);
    }

    [PostgreSqlFact]
    public async Task Removed_approved_line_keeps_its_reservation_reference_and_document_line_identity()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-amend-history-{Guid.NewGuid():N}";
        var seeded = await CreateTransferAsync(tenantId, approve: true, 5, 4);
        var retainedLine = seeded.Order.Lines[0];
        var removedLine = seeded.Order.Lines[1];
        var sourceReference = removedLine.ReservationSourceLineReference;

        await using (var amendmentContext = fixture.CreateContext(tenantId))
        {
            var scope = new StockMutationScope(seeded.CompanyId);
            await CreateService(amendmentContext, tenantId).AmendAsync(
                seeded.Order.Id,
                new CreateTransferOrderRequest(
                    seeded.CompanyId,
                    seeded.SourceLocationId,
                    seeded.DestinationLocationId,
                    [
                        new TransferOrderLineRequest(seeded.ItemId, retainedLine.Quantity, LineId: retainedLine.Id),
                        new TransferOrderLineRequest(seeded.ItemId, 3)
                    ]),
                scope);
        }

        await using var verify = fixture.CreateContext(tenantId);
        var reservation = await CreateStockService(verify, tenantId).GetReservationAsync(sourceReference);
        reservation.Should().NotBeNull();
        reservation!.SourceLineReference.Should().Be(sourceReference);
        reservation.Status.Should().Be(StockReservationStatus.Released);

        var documentLineIdentities = await verify.DocumentLineIdentities
            .Where(identity => identity.DocumentId == new DocumentIdentityId(seeded.Order.DocumentId))
            .ToListAsync();
        var removedIdentity = documentLineIdentities.SingleOrDefault(identity =>
            identity.Id.Value == removedLine.DocumentLineId);
        removedIdentity.Should().NotBeNull();
        (await verify.TransferOrderLines.AnyAsync(line => line.Id == removedLine.Id)).Should().BeFalse();
    }

    private async Task<TransferFixture> CreateTransferAsync(
        string tenantId,
        bool approve,
        params int[] quantities)
    {
        await using var setup = fixture.CreateContext(tenantId);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var company = new Company { Code = $"TO-{suffix}", LegalName = "Amendment Race Company" };
        setup.Companies.Add(company);
        await setup.SaveChangesAsync();

        var sourceBranch = new Branch { CompanyId = company.Id, Code = $"S-{suffix}", Name = "Source" };
        var destinationBranch = new Branch { CompanyId = company.Id, Code = $"D-{suffix}", Name = "Destination" };
        setup.Branches.AddRange(sourceBranch, destinationBranch);
        await setup.SaveChangesAsync();

        var source = new Location { BranchId = sourceBranch.Id, Name = $"Source-{suffix}" };
        var destination = new Location { BranchId = destinationBranch.Id, Name = $"Destination-{suffix}" };
        var item = new Item { ItemCode = $"TO-ITEM-{suffix}", Description = "Amendment race item" };
        setup.Locations.AddRange(source, destination);
        setup.Items.Add(item);
        await setup.SaveChangesAsync();

        if (approve)
        {
            await CreateStockService(setup, tenantId).ReceiveStockAsync(
                item.Id,
                source.Id,
                quantities.Sum() * 2,
                "Approved amendment fixture stock",
                mutationScope: new StockMutationScope(company.Id));
        }

        var order = await CreateService(setup, tenantId).CreateAsync(
            new CreateTransferOrderRequest(
                company.Id,
                source.Id,
                destination.Id,
                quantities.Select(quantity => new TransferOrderLineRequest(item.Id, quantity)).ToArray()),
            $"create-{Guid.NewGuid():N}");
        if (approve)
            await CreateService(setup, tenantId).ApproveAsync(order.Id, new StockMutationScope(company.Id));

        return new TransferFixture(
            company.Id,
            source.Id,
            destination.Id,
            item.Id,
            order);
    }

    private static TransferOrderService CreateService(InventoryDbContext context, string tenantId)
    {
        var tenant = new TestTenantContext(tenantId);
        var unitOfWork = new UnitOfWork(context);
        var dispatcher = new Mock<IWebhookDispatcher>().Object;
        var stockService = CreateStockService(context, tenant, unitOfWork, dispatcher);

        return new TransferOrderService(
            new Repository<TransferOrder>(context),
            new Repository<TransferOrderLine>(context),
            new Repository<TransferTransitEntry>(context),
            new Repository<DocumentIdentity>(context),
            new Repository<DocumentLineIdentity>(context),
            new Repository<Company>(context),
            new Repository<Branch>(context),
            new Repository<Location>(context),
            new Repository<Item>(context),
            unitOfWork,
            new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
            stockService,
            tenant,
            dispatcher,
            NullLogger<TransferOrderService>.Instance,
            new Repository<TransferTransitSettlement>(context));
    }

    private static StockService CreateStockService(InventoryDbContext context, string tenantId)
    {
        var tenant = new TestTenantContext(tenantId);
        return CreateStockService(context, tenant, new UnitOfWork(context), new Mock<IWebhookDispatcher>().Object);
    }

    private static StockService CreateStockService(
        InventoryDbContext context,
        TestTenantContext tenant,
        UnitOfWork unitOfWork,
        IWebhookDispatcher dispatcher) => new(
            new Repository<StockInHand>(context),
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            new Repository<Location>(context),
            new Repository<Branch>(context),
            unitOfWork,
            dispatcher,
            tenant,
            NullLogger<StockService>.Instance,
            new Repository<StockValuationBucket>(context),
            new Repository<StockValuationEntry>(context),
            new Repository<StockReservation>(context),
            new Repository<StockReservationAllocation>(context));

    private sealed record TransferFixture(
        int CompanyId,
        int SourceLocationId,
        int DestinationLocationId,
        int ItemId,
        TransferOrderView Order);
}
