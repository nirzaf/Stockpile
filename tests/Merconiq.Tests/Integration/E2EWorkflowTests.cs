using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public class StockWorkflowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public StockWorkflowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AuthClient => _factory.CreateAuthenticatedClient();

    [Fact]
    public async Task FullReceiveWorkflow_CreateItemAndLocation_ReceiveStock_VerifyStockInHand()
    {
        var client = AuthClient;

        // Create item via API
        var itemCode = $"WF-RCV-{Guid.NewGuid():N}".Substring(0, 18);
        var createResponse = await client.PostAsJsonAsync("/api/v1/items",
            new { ItemCode = itemCode, Description = "Workflow item", Rate = 20m });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Get item ID from location header
        var item = await GetItemByCodeAsync(itemCode);

        // Create location via DB
        Location loc;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            loc = new Location { Name = "WF-LOC-RCV" };
            db.Locations.Add(loc);
            await db.SaveChangesAsync();
        }

        // Receive stock
        var receiveCmd = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 50, Notes = "initial receive" };
        var receiveResponse = await client.PostAsJsonAsync("/api/v1/stock/receive", receiveCmd);
        receiveResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify stock
        var stock = await GetStockAsync(client, item.Id, loc.Id);
        stock.Quantity.Should().Be(50);
        (await LoadPersistedStockAsync(item.Id, loc.Id)).Quantity.Should().Be(50);
    }

    [Fact]
    public async Task FullSellWorkflow_ReceiveStock_SellStock_VerifyStockDecremented()
    {
        var client = AuthClient;

        // Setup
        var (item, loc) = await SeedItemAndLocationAsync();
        await ReceiveStockAsync(client, item.Id, loc.Id, 100);

        // Sell 30
        var sellCmd = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 30, Notes = "sale" };
        var sellResponse = await client.PostAsJsonAsync("/api/v1/stock/sell", sellCmd);
        sellResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify stock is now 70
        var stock = await GetStockAsync(client, item.Id, loc.Id);
        stock.Quantity.Should().Be(70);
        (await LoadPersistedStockAsync(item.Id, loc.Id)).Quantity.Should().Be(70);
    }

    [Fact]
    public async Task FullTransferWorkflow_ReceiveStock_TransferStock_VerifyBothLocations()
    {
        var client = AuthClient;

        var (item, loc1) = await SeedItemAndLocationAsync();
        await ReceiveStockAsync(client, item.Id, loc1.Id, 100);

        // Create second location
        Location loc2;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            loc2 = new Location { Name = "WF-LOC-DST", BranchId = loc1.BranchId };
            db.Locations.Add(loc2);
            await db.SaveChangesAsync();
        }

        // Transfer 40 from loc1 to loc2
        var transferCmd = new { ItemId = item.Id, FromLocationId = loc1.Id, ToLocationId = loc2.Id, Quantity = 40, Notes = "transfer" };
        var transferResponse = await client.PostAsJsonAsync("/api/v1/stock/transfer", transferCmd);
        transferResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify both locations
        (await GetStockAsync(client, item.Id, loc1.Id)).Quantity.Should().Be(60);
        (await GetStockAsync(client, item.Id, loc2.Id)).Quantity.Should().Be(40);
        (await LoadPersistedStockAsync(item.Id, loc1.Id)).Quantity.Should().Be(60);
        (await LoadPersistedStockAsync(item.Id, loc2.Id)).Quantity.Should().Be(40);
    }

    [Fact]
    public async Task SellMoreThanAvailable_Returns409_StockUnchanged()
    {
        var client = AuthClient;

        var (item, loc) = await SeedItemAndLocationAsync();
        await ReceiveStockAsync(client, item.Id, loc.Id, 10);

        // Try to sell 50 (only 10 available)
        var sellCmd = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 50, Notes = "oversell" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/sell", sellCmd);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await LoadPersistedStockAsync(item.Id, loc.Id)).Quantity.Should().Be(10);
        await AssertTransactionAsync(item.Id, TransactionType.Receive, 10, loc.Id, loc.Id);
        (await CountTransactionsAsync(item.Id, TransactionType.Sell)).Should().Be(0);
    }

    [Fact]
    public async Task StockTransactions_AfterOperations_AllRecorded()
    {
        var client = AuthClient;

        var (item, loc) = await SeedItemAndLocationAsync();
        await ReceiveStockAsync(client, item.Id, loc.Id, 50);

        // Sell some
        var sellCmd = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 10, Notes = "audit test" };
        await client.PostAsJsonAsync("/api/v1/stock/sell", sellCmd);

        // Check transactions
        var txResponse = await client.GetAsync("/api/v1/stock/transactions");
        txResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var transactions = await txResponse.Content.ReadFromJsonAsync<ApiResponse<List<StockTransaction>>>();
        transactions.Should().NotBeNull();
        transactions!.Success.Should().BeTrue();
        transactions.Data.Should().Contain(transaction =>
            transaction.ItemId == item.Id && transaction.TransactionType == TransactionType.Receive && transaction.Quantity == 50);
        transactions.Data.Should().Contain(transaction =>
            transaction.ItemId == item.Id && transaction.TransactionType == TransactionType.Sell &&
            transaction.Quantity == 10 && transaction.FromLocationId == loc.Id);
        (await CountTransactionsAsync(item.Id, TransactionType.Receive)).Should().Be(1);
        (await CountTransactionsAsync(item.Id, TransactionType.Sell)).Should().Be(1);
    }

    [Fact]
    public async Task MultiLocation_StockBalances_CorrectAfterOperations()
    {
        var client = AuthClient;

        var (item, loc1) = await SeedItemAndLocationAsync();

        Location loc2;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            loc2 = new Location { Name = "ML-LOC2", BranchId = loc1.BranchId };
            db.Locations.Add(loc2);
            await db.SaveChangesAsync();
        }

        // Receive 100 at loc1
        await ReceiveStockAsync(client, item.Id, loc1.Id, 100);

        // Transfer 30 to loc2
        var transferCmd = new { ItemId = item.Id, FromLocationId = loc1.Id, ToLocationId = loc2.Id, Quantity = 30, Notes = "ml" };
        var transferResponse = await client.PostAsJsonAsync("/api/v1/stock/transfer", transferCmd);
        transferResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Sell 20 from loc1
        var sellCmd = new { ItemId = item.Id, LocationId = loc1.Id, Quantity = 20, Notes = "ml" };
        var sellResponse = await client.PostAsJsonAsync("/api/v1/stock/sell", sellCmd);
        sellResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // loc1 should have 50, loc2 should have 30
        (await GetStockAsync(client, item.Id, loc1.Id)).Quantity.Should().Be(50);
        (await GetStockAsync(client, item.Id, loc2.Id)).Quantity.Should().Be(30);
        (await LoadPersistedStockAsync(item.Id, loc1.Id)).Quantity.Should().Be(50);
        (await LoadPersistedStockAsync(item.Id, loc2.Id)).Quantity.Should().Be(30);
    }

    // === Helpers ===

    private async Task<(Item item, Location loc)> SeedItemAndLocationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var company = new Company
        {
            Code = $"WF-{Guid.NewGuid():N}".Substring(0, 10),
            LegalName = "Workflow company"
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var branch = new Branch
        {
            CompanyId = company.Id,
            Code = $"BR-{Guid.NewGuid():N}".Substring(0, 10),
            Name = "Workflow branch"
        };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        var item = new Item { ItemCode = $"WF-{Guid.NewGuid():N}".Substring(0, 12), Description = "WF item", Rate = 10m };
        var loc = new Location { Name = $"WF-{Guid.NewGuid():N}".Substring(0, 12), BranchId = branch.Id };
        db.Items.Add(item);
        db.Locations.Add(loc);
        await db.SaveChangesAsync();
        return (item, loc);
    }

    private async Task ReceiveStockAsync(HttpClient client, int itemId, int locationId, int qty)
    {
        var response = await client.PostAsJsonAsync("/api/v1/stock/receive",
            new { ItemId = itemId, LocationId = locationId, Quantity = qty, Notes = "workflow receive" });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<StockInHand> GetStockAsync(HttpClient client, int itemId, int locationId)
    {
        var response = await client.GetAsync($"/api/v1/stock/in-hand/{itemId}/{locationId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<StockInHand>>();
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.Data.Should().NotBeNull();
        return body.Data!;
    }

    private async Task<StockInHand> LoadPersistedStockAsync(int itemId, int locationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return await db.StockInHand.AsNoTracking()
            .SingleAsync(stock => stock.ItemId == itemId && stock.LocationId == locationId);
    }

    private async Task<int> CountTransactionsAsync(int itemId, TransactionType type)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return await db.StockTransactions.CountAsync(transaction =>
            transaction.ItemId == itemId && transaction.TransactionType == type);
    }

    private async Task AssertTransactionAsync(
        int itemId,
        TransactionType type,
        int quantity,
        int fromLocationId,
        int toLocationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var transaction = await db.StockTransactions.SingleAsync(candidate =>
            candidate.ItemId == itemId && candidate.TransactionType == type);
        transaction.Quantity.Should().Be(quantity);
        transaction.FromLocationId.Should().Be(fromLocationId);
        transaction.ToLocationId.Should().Be(toLocationId);
    }

    private async Task<Item> GetItemByCodeAsync(string code)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return await db.Items.FirstAsync(i => i.ItemCode == code);
    }
}

public class PurchaseOrderWorkflowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PurchaseOrderWorkflowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FullPOWorkflow_CreateSupplierAndItem_VerifyStatusFlow()
    {
        // Seed supplier and item
        Item item;
        Supplier supplier;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            supplier = new Supplier { Name = "PO-SUPPLIER" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            item = new Item { ItemCode = "PO-ITEM", Description = "PO item", Rate = 25m, SupplierId = supplier.Id };
            db.Items.Add(item);

            var poService = scope.ServiceProvider.GetRequiredService<IPurchaseOrderService>();
            var po = await poService.CreateAsync(new PurchaseOrder
            {
                PONumber = $"PO-{Guid.NewGuid():N}".Substring(0, 15),
                SupplierId = supplier.Id,
            },
            [new OrderDetail { ItemId = item.Id, Quantity = 10, UnitPrice = 25m }],
            $"po-create-{Guid.NewGuid():N}");

            // Verify the service-computed PO exists with correct status
            var savedPo = await db.PurchaseOrders
                .Include(p => p.OrderDetails)
                .AsNoTracking()
                .FirstAsync(p => p.Id == po.Id);
            savedPo.Status.Should().Be(PurchaseOrderStatus.Pending);
            savedPo.TotalAmount.Should().Be(250m);
            savedPo.OrderDetails.Should().ContainSingle(detail => detail.Quantity == 10 && detail.UnitPrice == 25m);
        }
    }

    [Fact]
    public async Task POStatusTransitions_PendingToApproved()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var supplier = new Supplier { Name = "PO-SUP-2" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var poService = scope.ServiceProvider.GetRequiredService<IPurchaseOrderService>();
        var po = await poService.CreateAsync(new PurchaseOrder
        {
            PONumber = $"PO-{Guid.NewGuid():N}".Substring(0, 15),
            SupplierId = supplier.Id,
        }, [], $"po-create-{Guid.NewGuid():N}");

        // Update status
        await poService.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Approved));

        var updated = await db.PurchaseOrders.AsNoTracking().FirstAsync(p => p.Id == po.Id);
        updated.Status.Should().Be(PurchaseOrderStatus.Approved);
    }

    [Fact]
    public async Task POStatusTransitions_InvalidStatus_IsRejectedByTheService()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var supplier = new Supplier { Name = $"PO-SUP-INVALID-{Guid.NewGuid():N}"[..20] };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var poService = scope.ServiceProvider.GetRequiredService<IPurchaseOrderService>();
        var po = await poService.CreateAsync(new PurchaseOrder
        {
            PONumber = $"PO-{Guid.NewGuid():N}"[..15],
            SupplierId = supplier.Id
        }, [], $"po-create-{Guid.NewGuid():N}");

        await FluentActions.Invoking(() => poService.UpdateStatusAsync(po.Id, "NotAStatus"))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task POStatusTransitions_MissingOrder_IsRejectedByTheService()
    {
        using var scope = _factory.Services.CreateScope();
        var poService = scope.ServiceProvider.GetRequiredService<IPurchaseOrderService>();

        await FluentActions.Invoking(() => poService.UpdateStatusAsync(int.MaxValue, nameof(PurchaseOrderStatus.Approved)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Purchase order not found");
    }

    [Fact]
    public async Task POWithMultipleDetails_TotalCalculatedCorrectly()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var supplier = new Supplier { Name = "PO-SUP-3" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var item1 = new Item { ItemCode = "PO-D1", Description = "Detail 1", Rate = 10m };
        var item2 = new Item { ItemCode = "PO-D2", Description = "Detail 2", Rate = 20m };
        db.Items.AddRange(item1, item2);
        await db.SaveChangesAsync();

        var poService = scope.ServiceProvider.GetRequiredService<IPurchaseOrderService>();
        var po = await poService.CreateAsync(new PurchaseOrder
        {
            PONumber = $"PO-{Guid.NewGuid():N}".Substring(0, 15),
            SupplierId = supplier.Id,
        },
        [
            new OrderDetail { ItemId = item1.Id, Quantity = 5, UnitPrice = 10m },
            new OrderDetail { ItemId = item2.Id, Quantity = 3, UnitPrice = 20m }
        ], $"po-create-{Guid.NewGuid():N}");

        var saved = await db.PurchaseOrders
            .Include(p => p.OrderDetails)
            .AsNoTracking()
            .FirstAsync(p => p.Id == po.Id);
        saved.TotalAmount.Should().Be(110m);
        saved.OrderDetails.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeletePO_CancelsAndRetainsTheNumberAndDocumentIdentity()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var supplier = new Supplier { Name = "PO-SUP-DEL" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var poService = scope.ServiceProvider.GetRequiredService<IPurchaseOrderService>();
        var po = await poService.CreateAsync(new PurchaseOrder
        {
            PONumber = $"PO-{Guid.NewGuid():N}".Substring(0, 15),
            SupplierId = supplier.Id,
        }, [], $"po-create-{Guid.NewGuid():N}");

        await poService.DeleteAsync(po.Id);

        var retained = await db.PurchaseOrders
            .Include(order => order.DocumentIdentity)
            .AsNoTracking()
            .SingleAsync(order => order.Id == po.Id);
        retained.Status.Should().Be(PurchaseOrderStatus.Cancelled);
        retained.DocumentIdentity.HumanNumber.Should().Be(retained.PONumber);
        retained.DocumentIdentity.Status.Should().Be(DocumentLifecycleStatus.Cancelled);
    }
}

public class ValidationWorkflowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ValidationWorkflowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AuthClient => _factory.CreateAuthenticatedClient();

    [Fact]
    public async Task CreateItem_WithEmptyCode_Returns400OrError()
    {
        var client = AuthClient;

        var command = new { ItemCode = "", Description = "no code", Rate = 10m };
        var response = await client.PostAsJsonAsync("/api/v1/items", command);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReceiveStock_WithZeroQuantity_Returns400OrConflict()
    {
        var client = AuthClient;

        // Need valid item and location
        Item item; Location loc;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            item = new Item { ItemCode = "VQ-ITEM", Description = "vq", Rate = 10m };
            loc = new Location { Name = "VQ-LOC" };
            db.Items.Add(item);
            db.Locations.Add(loc);
            await db.SaveChangesAsync();
        }

        var command = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 0, Notes = "zero" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/receive", command);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TransferStock_SameLocation_Returns400OrConflict()
    {
        var client = AuthClient;

        Item item; Location loc;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            item = new Item { ItemCode = "SL-ITEM", Description = "sl", Rate = 10m };
            loc = new Location { Name = "SL-LOC" };
            db.Items.Add(item);
            db.Locations.Add(loc);
            db.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = loc.Id, Quantity = 50 });
            await db.SaveChangesAsync();
        }

        var command = new { ItemId = item.Id, FromLocationId = loc.Id, ToLocationId = loc.Id, Quantity = 10, Notes = "same" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/transfer", command);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SellStock_NegativeQuantity_Returns400OrConflict()
    {
        var client = AuthClient;

        Item item; Location loc;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            item = new Item { ItemCode = "NQ-ITEM", Description = "nq", Rate = 10m };
            loc = new Location { Name = "NQ-LOC" };
            db.Items.Add(item);
            db.Locations.Add(loc);
            db.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = loc.Id, Quantity = 50 });
            await db.SaveChangesAsync();
        }

        var command = new { ItemId = item.Id, LocationId = loc.Id, Quantity = -5, Notes = "negative" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/sell", command);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
