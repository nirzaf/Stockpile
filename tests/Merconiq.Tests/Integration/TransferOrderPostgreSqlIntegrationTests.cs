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
public sealed class TransferOrderPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Approval_reserves_source_stock_without_moving_it_and_cancel_releases_the_reservation()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-order-{Guid.NewGuid():N}";
        int companyId;
        int sourceLocationId;
        int destinationLocationId;
        int itemId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company { Code = $"TO-{Guid.NewGuid():N}"[..12], LegalName = "Transfer company" };
            setup.Companies.Add(company);
            await setup.SaveChangesAsync();
            var sourceBranch = new Branch { CompanyId = company.Id, Code = "SOURCE", Name = "Source" };
            var destinationBranch = new Branch { CompanyId = company.Id, Code = "DEST", Name = "Destination" };
            setup.Branches.AddRange(sourceBranch, destinationBranch);
            await setup.SaveChangesAsync();
            var source = new Location { BranchId = sourceBranch.Id, Name = "Source warehouse" };
            var destination = new Location { BranchId = destinationBranch.Id, Name = "Destination warehouse" };
            var item = new Item { ItemCode = $"TO-ITEM-{Guid.NewGuid():N}", Description = "Transfer item" };
            setup.Locations.AddRange(source, destination);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();
            setup.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = source.Id, Quantity = 10 });
            await setup.SaveChangesAsync();
            companyId = company.Id;
            sourceLocationId = source.Id;
            destinationLocationId = destination.Id;
            itemId = item.Id;
        }

        await using (var operation = fixture.CreateContext(tenantId))
        {
            var orders = CreateService(operation, tenantId);
            var order = await orders.CreateAsync(new CreateTransferOrderRequest(
                companyId,
                sourceLocationId,
                destinationLocationId,
                [new TransferOrderLineRequest(itemId, 3)]), "transfer-create-1");
            await orders.ApproveAsync(order.Id, new StockMutationScope(companyId));

            (await operation.StockTransactions.CountAsync()).Should().Be(0);
            (await operation.StockInHand.SingleAsync(stockRow =>
                    stockRow.ItemId == itemId && stockRow.LocationId == sourceLocationId))
                .Should().Match<StockInHand>(stockRow => stockRow.Quantity == 10 && stockRow.ReservedQuantity == 3);
            (await operation.StockReservations.SingleAsync()).Should().Match<StockReservation>(reservation =>
                reservation.Quantity == 3 && reservation.Status == StockReservationStatus.Active);

            await using var cancellation = fixture.CreateContext(tenantId);
            await CreateService(cancellation, tenantId)
                .CancelAsync(order.Id, new StockMutationScope(companyId));
            (await cancellation.StockInHand.SingleAsync(stockRow =>
                    stockRow.ItemId == itemId && stockRow.LocationId == sourceLocationId))
                .ReservedQuantity.Should().Be(0);
            (await cancellation.StockReservations.SingleAsync()).Status.Should().Be(StockReservationStatus.Released);
            (await cancellation.TransferOrders.SingleAsync()).Status.Should().Be(TransferOrderStatus.Cancelled);
        }
    }

    private static TransferOrderService CreateService(InventoryDbContext context, string tenantId)
    {
        var tenant = new TestTenantContext(tenantId);
        var unitOfWork = new UnitOfWork(context);
        var stock = new StockService(
            new Repository<StockInHand>(context),
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            new Repository<Location>(context),
            new Repository<Branch>(context),
            unitOfWork,
            new Mock<IWebhookDispatcher>().Object,
            tenant,
            NullLogger<StockService>.Instance,
            new Repository<StockValuationBucket>(context),
            new Repository<StockValuationEntry>(context),
            new Repository<StockReservation>(context));
        return new TransferOrderService(
            new Repository<TransferOrder>(context),
            new Repository<TransferOrderLine>(context),
            new Repository<DocumentIdentity>(context),
            new Repository<DocumentLineIdentity>(context),
            new Repository<Company>(context),
            new Repository<Branch>(context),
            new Repository<Location>(context),
            new Repository<Item>(context),
            unitOfWork,
            new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
            stock,
            tenant,
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);
    }
}
