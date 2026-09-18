using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class ApiContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Unmapped_host_is_rejected_before_authentication()
    {
        using var client = _factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/items");
        request.Headers.Host = "unmapped.example";

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stock_mutation_requires_post_capability()
    {
        using var client = _factory.CreateAuthenticatedClient(role: "Viewer");

        var response = await client.PostAsJsonAsync(
            "/api/v1/stock/receive",
            new { ItemId = 1, LocationId = 1, Quantity = 1, Notes = "role check" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Stock_read_is_available_to_viewers()
    {
        using var client = _factory.CreateAuthenticatedClient(role: "Viewer");

        (await client.GetAsync("/api/v1/stock/in-hand")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = (await users.FindByNameAsync("viewer@test-tenant.test"))!;
            var company = new Company
            {
                Code = $"VIEW-{Guid.NewGuid():N}"[..12],
                LegalName = "Viewer company"
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            db.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = company.Id,
                UserId = viewer.Id,
                Capabilities = CompanyCapability.View,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/v1/stock/in-hand");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_configuration_is_restricted_to_tenant_administrators()
    {
        using var client = _factory.CreateAuthenticatedClient(role: "Viewer");

        var read = await client.GetAsync("/api/v1/webhooks");
        var edit = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Url = "https://example.com/hooks",
            EventType = "StockChanged",
            IsActive = true
        });

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        edit.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var adminClient = _factory.CreateAuthenticatedClient(role: "Admin");
        (await adminClient.GetAsync("/api/v1/webhooks")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_delivery_diagnostics_are_bounded_tenant_scoped_and_redacted()
    {
        var eventType = $"Test.Delivery.{Guid.NewGuid():N}";
        var oversizedEventType = $"{eventType}.OversizedPayload";
        var privateEventId = Guid.NewGuid();
        var oversizedEventId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            db.WebhookDeliveries.AddRange(
                new WebhookDelivery
                {
                    TenantId = "test-tenant",
                    EventId = privateEventId,
                    SubscriptionId = 10,
                    EventType = eventType,
                    Payload = "private-payload-marker",
                    Status = WebhookDeliveryStatus.DeadLetter,
                    AttemptCount = 5,
                    LastStatusCode = 503,
                    LastResponse = "private-response-marker",
                    LastError = "private-error-marker",
                    CreatedAt = createdAt
                },
                new WebhookDelivery
                {
                    TenantId = "test-tenant",
                    EventId = oversizedEventId,
                    SubscriptionId = 12,
                    EventType = oversizedEventType,
                    Payload = "oversized-payload-private-marker",
                    Status = WebhookDeliveryStatus.DeadLetter,
                    LastError = WebhookPayloadPolicy.OversizedEnvelopeDiagnostic,
                    CreatedAt = createdAt.AddSeconds(1)
                });
            await db.SaveChangesAsync();

            var otherTenantContext = new TenantContext();
            otherTenantContext.SetTenant("other-tenant");
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<InventoryDbContext>>();
            await using var otherTenantDb = new InventoryDbContext(options, otherTenantContext);
            otherTenantDb.WebhookDeliveries.Add(new WebhookDelivery
            {
                EventId = Guid.NewGuid(),
                SubscriptionId = 11,
                EventType = "cross-tenant-event-marker",
                Payload = "cross-tenant-payload-marker"
            });
            await otherTenantDb.SaveChangesAsync();
        }

        using var viewerClient = _factory.CreateAuthenticatedClient(role: "Viewer");
        (await viewerClient.GetAsync("/api/v1/webhooks/deliveries")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        using var adminClient = _factory.CreateAuthenticatedClient(role: "Admin");
        (await adminClient.GetAsync("/api/v1/webhooks/deliveries?pageSize=101")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        using var response = await adminClient.GetAsync("/api/v1/webhooks/deliveries?page=1&pageSize=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(eventType);
        body.Should().Contain(oversizedEventType);
        body.Should().NotContain("cross-tenant-event-marker");
        body.Should().NotContain("private-payload-marker");
        body.Should().NotContain("private-response-marker");
        body.Should().NotContain("private-error-marker");
        body.Should().NotContain("oversized-payload-private-marker");
        body.Should().NotContain("cross-tenant-payload-marker");
        using var document = JsonDocument.Parse(body);
        var page = document.RootElement.GetProperty("data");
        var deliveries = page.GetProperty("deliveries").EnumerateArray().ToArray();
        var privateDelivery = deliveries.Single(delivery =>
            delivery.GetProperty("eventId").GetGuid() == privateEventId);
        privateDelivery.GetProperty("failureReason").ValueKind.Should().Be(JsonValueKind.Null);
        var oversizedDelivery = deliveries.Single(delivery =>
            delivery.GetProperty("eventId").GetGuid() == oversizedEventId);
        oversizedDelivery.GetProperty("failureReason").GetString()
            .Should().Be(WebhookPayloadPolicy.OversizedEnvelopeDiagnostic);
        WebhookPayloadPolicy.OversizedEnvelopeDiagnostic.Length.Should().BeLessThan(4096);
    }
}
