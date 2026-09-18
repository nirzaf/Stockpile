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

public sealed class PurchaseOrderObligationsApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Obligations_api_enforces_company_and_tenant_scope_and_exposes_no_receipt_progress()
    {
        using var client = factory.CreateAuthenticatedClient("Manager");
        var seeded = await SeedScopeAsync(Guid.NewGuid().ToString("N")[..8]);

        (await client.GetAsync(ObligationsPath(seeded.CompanyBId, seeded.OrderBId)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(ObligationsPath(seeded.CompanyAId, seeded.OrderBId)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "an order mapped to another company is indistinguishable from a missing order");

        using var response = await client.GetAsync(ObligationsPath(seeded.CompanyAId, seeded.OrderAId));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var data = body!.RootElement.GetProperty("data");
        data.GetProperty("purchaseOrderId").GetInt32().Should().Be(seeded.OrderAId);
        data.GetProperty("orderedQuantity").GetInt64().Should().Be(5);
        var line = data.GetProperty("lines")[0];
        line.GetProperty("orderedQuantity").GetInt32().Should().Be(5);
        line.TryGetProperty("receivedQuantity", out _).Should().BeFalse();
        line.TryGetProperty("acceptedQuantity", out _).Should().BeFalse();
        line.TryGetProperty("rejectedQuantity", out _).Should().BeFalse();
        data.TryGetProperty("outstandingQuantity", out _).Should().BeFalse();
        data.TryGetProperty("lifecycleStatus", out _).Should().BeFalse();
        data.TryGetProperty("progressState", out _).Should().BeFalse();

        using var otherTenant = factory.CreateAuthenticatedClient("Manager", "other-tenant");
        (await otherTenant.GetAsync(ObligationsPath(seeded.CompanyAId, seeded.OrderAId)))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Source_less_progress_post_is_not_routed_and_cannot_change_ordered_obligations()
    {
        using var client = factory.CreateAuthenticatedClient("Manager");
        var seeded = await SeedScopeAsync(Guid.NewGuid().ToString("N")[..8]);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            LineProgressPath(seeded.CompanyAId, seeded.OrderAId, seeded.LineAId))
        {
            Content = JsonContent.Create(new
            {
                receivedQuantity = 3,
                acceptedQuantity = 1,
                rejectedQuantity = 1
            })
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "this partial API exposes no source-less progress writer");
        using var obligationsResponse = await client.GetAsync(
            ObligationsPath(seeded.CompanyAId, seeded.OrderAId));
        obligationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = await obligationsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var data = body!.RootElement.GetProperty("data");
        data.GetProperty("orderedQuantity").GetInt64().Should().Be(5);
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
        var supplier = new Supplier { Name = "PO obligations supplier" };
        var item = new Item
        {
            ItemCode = $"OBL-{suffix}",
            Description = "Synthetic obligations item",
            Rate = 2m
        };
        db.AddRange(companyA, companyB, supplier, item);
        await db.SaveChangesAsync();

        db.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyA.Id,
            UserId = manager!.Id,
            Capabilities = CompanyCapability.View,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var orderA = AddApprovedOrder(db, companyA.Id, supplier.Id, item.Id, suffix + "A");
        var orderB = AddApprovedOrder(db, companyB.Id, supplier.Id, item.Id, suffix + "B");
        await db.SaveChangesAsync();

        return new TestScope(companyA.Id, companyB.Id, orderA.Order.Id, orderA.Line.Id,
            orderB.Order.Id);
    }

    private static (PurchaseOrder Order, OrderDetail Line) AddApprovedOrder(
        InventoryDbContext db,
        int companyId,
        int supplierId,
        int itemId,
        string suffix)
    {
        var number = $"PO-OBL-{suffix}";
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
            "PurchaseOrderObligationsApiTests");
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

    private static string ObligationsPath(int companyId, int orderId) =>
        $"/api/v1/companies/{companyId}/purchase-orders/{orderId}/obligations";

    private static string LineProgressPath(int companyId, int orderId, int lineId) =>
        $"/api/v1/companies/{companyId}/purchase-orders/{orderId}/lines/{lineId}/progress";

    private sealed record TestScope(
        int CompanyAId,
        int CompanyBId,
        int OrderAId,
        int LineAId,
        int OrderBId);
}
