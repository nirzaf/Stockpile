using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class PurchaseOrderProgressApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Line_progress_api_enforces_company_and_tenant_scope_and_replays_idempotently()
    {
        using var client = factory.CreateAuthenticatedClient("Manager");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var seeded = await SeedScopeAsync(suffix);

        (await client.GetAsync(ProgressPath(seeded.CompanyBId, seeded.OrderBId)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(ProgressPath(seeded.CompanyAId, seeded.OrderBId)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "an order mapped to another company is indistinguishable from a missing order");

        var path = LineProgressPath(seeded.CompanyAId, seeded.OrderAId, seeded.LineAId);
        var change = new PurchaseOrderLineProgressChange(ReceivedQuantity: 3, AcceptedQuantity: 1, RejectedQuantity: 1);
        const string idempotencyKey = "po-line-progress-replay";

        using var first = await PostProgressAsync(client, path, idempotencyKey, change);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        using var replay = await PostProgressAsync(client, path, idempotencyKey, change);
        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());

        using var replayBody = await replay.Content.ReadFromJsonAsync<JsonDocument>();
        var replayProgress = replayBody!.RootElement.GetProperty("data");
        replayProgress.GetProperty("lifecycleStatus").GetInt32().Should().Be((int)PurchaseOrderStatus.Approved);
        replayProgress.GetProperty("progressState").GetInt32().Should().Be((int)PurchaseOrderProgressState.PartiallyReceived);
        replayProgress.GetProperty("revision").GetInt32().Should().Be(1);
        replayProgress.GetProperty("orderedQuantity").GetInt64().Should().Be(5);
        replayProgress.GetProperty("receivedQuantity").GetInt64().Should().Be(3);
        replayProgress.GetProperty("acceptedQuantity").GetInt64().Should().Be(1);
        replayProgress.GetProperty("rejectedQuantity").GetInt64().Should().Be(1);
        replayProgress.GetProperty("outstandingQuantity").GetInt64().Should().Be(2);
        replayProgress.GetProperty("awaitingInspectionQuantity").GetInt64().Should().Be(1);

        using var reusedKey = await PostProgressAsync(
            client,
            path,
            idempotencyKey,
            new PurchaseOrderLineProgressChange(1, 1, 0));
        reusedKey.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var overReceipt = await PostProgressAsync(
            client,
            path,
            "po-line-progress-over-receipt",
            new PurchaseOrderLineProgressChange(3, 0, 0));
        overReceipt.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var current = await client.GetAsync(ProgressPath(seeded.CompanyAId, seeded.OrderAId));
        current.StatusCode.Should().Be(HttpStatusCode.OK);
        using var currentBody = await current.Content.ReadFromJsonAsync<JsonDocument>();
        var currentProgress = currentBody!.RootElement.GetProperty("data");
        currentProgress.GetProperty("revision").GetInt32().Should().Be(1);
        currentProgress.GetProperty("receivedQuantity").GetInt64().Should().Be(3);
        currentProgress.GetProperty("lifecycleStatus").GetInt32().Should().Be((int)PurchaseOrderStatus.Approved);

        using var otherTenant = factory.CreateAuthenticatedClient("Manager", "other-tenant");
        (await otherTenant.GetAsync(ProgressPath(seeded.CompanyAId, seeded.OrderAId)))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Line_progress_api_checks_company_scope_before_rejecting_invalid_quantities()
    {
        using var client = factory.CreateAuthenticatedClient("Manager");
        var seeded = await SeedScopeAsync(Guid.NewGuid().ToString("N")[..8]);
        var path = LineProgressPath(seeded.CompanyBId, seeded.OrderBId, seeded.LineBId);

        using var response = await PostProgressAsync(
            client,
            path,
            "unauthorized-invalid-quantities",
            new PurchaseOrderLineProgressChange(ReceivedQuantity: -1, AcceptedQuantity: 0, RejectedQuantity: 0));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Line_progress_api_model_validation_rejects_negative_and_empty_outcomes()
    {
        using var client = factory.CreateAuthenticatedClient("Manager");
        var seeded = await SeedScopeAsync(Guid.NewGuid().ToString("N")[..8]);
        var path = LineProgressPath(seeded.CompanyAId, seeded.OrderAId, seeded.LineAId);

        using var negative = await PostProgressAsync(
            client,
            path,
            "negative-progress-quantity",
            new PurchaseOrderLineProgressChange(ReceivedQuantity: -1, AcceptedQuantity: 0, RejectedQuantity: 0));
        negative.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var empty = await PostProgressAsync(
            client,
            path,
            "empty-progress-outcome",
            new PurchaseOrderLineProgressChange(ReceivedQuantity: 0, AcceptedQuantity: 0, RejectedQuantity: 0));
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<TestScope> SeedScopeAsync(string suffix)
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<InventoryDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var manager = await userManager.FindByNameAsync("manager@test-tenant.test");
        manager.Should().NotBeNull();

        var companyA = new Company
        {
            Code = $"POA-{suffix}",
            LegalName = "Authorized PO company",
            BaseCurrency = "USD"
        };
        var companyB = new Company
        {
            Code = $"POB-{suffix}",
            LegalName = "Restricted PO company",
            BaseCurrency = "USD"
        };
        var supplier = new Supplier { Name = "PO progress supplier" };
        var item = new Item
        {
            ItemCode = $"PROG-{suffix}",
            Description = "Synthetic progress item",
            Rate = 2m
        };
        db.AddRange(companyA, companyB, supplier, item);
        await db.SaveChangesAsync();

        db.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyA.Id,
            UserId = manager!.Id,
            Capabilities = CompanyCapability.View | CompanyCapability.Post,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var orderA = AddApprovedOrder(db, companyA.Id, supplier.Id, item.Id, suffix + "A");
        var orderB = AddApprovedOrder(db, companyB.Id, supplier.Id, item.Id, suffix + "B");
        await db.SaveChangesAsync();

        return new TestScope(
            companyA.Id,
            companyB.Id,
            orderA.Order.Id,
            orderA.Line.Id,
            orderB.Order.Id,
            orderB.Line.Id);
    }

    private static (PurchaseOrder Order, OrderDetail Line) AddApprovedOrder(
        InventoryDbContext db,
        int companyId,
        int supplierId,
        int itemId,
        string suffix)
    {
        var number = $"PO-PROG-{suffix}";
        var order = new PurchaseOrder
        {
            PONumber = number,
            OrderDate = DateTime.UtcNow,
            SupplierId = supplierId,
            Status = PurchaseOrderStatus.Approved,
            CommercialVersion = 1,
            ApprovedCommercialVersion = 1,
            ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}",
            CurrencyScale = 2
        };
        var document = DocumentIdentity.Create(
            order.DocumentId,
            db.CurrentTenantId,
            companyId,
            "PurchaseOrder",
            number,
            DateTime.UtcNow.Year,
            DocumentLifecycleStatus.Active,
            "PurchaseOrderProgressApiTests");
        document.PurchaseOrder = order;
        order.DocumentIdentity = document;

        var line = new OrderDetail
        {
            PurchaseOrder = order,
            ItemId = itemId,
            Quantity = 5,
            UnitPrice = 2m,
            CurrencyScale = 2,
            Direction = DocumentLineDirection.Charge
        };
        var lineIdentity = DocumentLineIdentity.Create(
            line.DocumentLineId,
            order.DocumentId,
            db.CurrentTenantId,
            companyId,
            "PurchaseOrderLine");
        lineIdentity.OrderDetail = line;
        line.DocumentLineIdentity = lineIdentity;
        order.OrderDetails.Add(line);
        document.Lines.Add(lineIdentity);

        db.DocumentIdentities.Add(document);
        db.PurchaseOrders.Add(order);
        db.DocumentLineIdentities.Add(lineIdentity);
        db.OrderDetails.Add(line);
        return (order, line);
    }

    private static string ProgressPath(int companyId, int orderId) =>
        $"/api/v1/companies/{companyId}/purchase-orders/{orderId}/progress";

    private static string LineProgressPath(int companyId, int orderId, int lineId) =>
        $"/api/v1/companies/{companyId}/purchase-orders/{orderId}/lines/{lineId}/progress";

    private static Task<HttpResponseMessage> PostProgressAsync(
        HttpClient client,
        string path,
        string key,
        PurchaseOrderLineProgressChange change)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(change)
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private sealed record TestScope(
        int CompanyAId,
        int CompanyBId,
        int OrderAId,
        int LineAId,
        int OrderBId,
        int LineBId);
}
