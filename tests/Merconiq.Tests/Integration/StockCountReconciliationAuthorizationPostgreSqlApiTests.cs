using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockCountReconciliationAuthorizationPostgreSqlApiTests(
    PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture, applicationName: "merconiq-stock-count-reconciliation-auth-api");

    [PostgreSqlFact]
    public async Task View_grant_scopes_reconciliation_to_the_persisted_company_item_and_lot()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var requestedExpiry = new DateTime(2031, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        var otherExpiry = new DateTime(2032, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        const string requestedLot = "RECON-REQUESTED-LOT";
        const string otherLot = "RECON-OTHER-LOT";

        var companyA = new Company
        {
            Code = $"RECON-A-{suffix[..10]}",
            LegalName = "Synthetic reconciliation company A",
            BaseCurrency = "USD"
        };
        var companyB = new Company
        {
            Code = $"RECON-B-{suffix[..10]}",
            LegalName = "Synthetic reconciliation company B",
            BaseCurrency = "USD"
        };
        var branchA = new Branch
        {
            Company = companyA,
            Code = $"RECON-A-{suffix[..10]}",
            Name = "Reconciliation branch A"
        };
        var branchB = new Branch
        {
            Company = companyB,
            Code = $"RECON-B-{suffix[..10]}",
            Name = "Reconciliation branch B"
        };
        var locationA = new Location { Branch = branchA, Name = $"Reconciliation location A {suffix}" };
        var locationB = new Location { Branch = branchB, Name = $"Reconciliation location B {suffix}" };
        var requestedItem = new Item
        {
            ItemCode = $"RECON-ITEM-{suffix[..10]}",
            Description = "Requested reconciliation item",
            ReorderLevel = 0
        };
        var otherItem = new Item
        {
            ItemCode = $"RECON-OTHER-ITEM-{suffix[..10]}",
            Description = "Unrequested reconciliation item",
            ReorderLevel = 0
        };

        var positions = new[]
        {
            CreatePosition(requestedItem, locationA, 11, requestedLot, requestedExpiry, 2, 1),
            CreatePosition(requestedItem, locationA, 23, otherLot, requestedExpiry),
            CreatePosition(requestedItem, locationA, 29, requestedLot, otherExpiry),
            CreatePosition(otherItem, locationA, 31, requestedLot, requestedExpiry),
            CreatePosition(requestedItem, locationB, 37, requestedLot, requestedExpiry)
        };

        await using (var seed = fixture.CreateContext("test-tenant"))
        {
            seed.AddRange(companyA, companyB, branchA, branchB, locationA, locationB, requestedItem, otherItem);
            foreach (var position in positions)
            {
                seed.StockInHand.Add(position.Balance);
                seed.StockTransactions.Add(position.Movement);
            }

            await seed.SaveChangesAsync();
        }

        var auditor = await _factory.EnsurePersonaUserAsync("RestrictedAuditor", suffix);
        await using (var grant = fixture.CreateContext("test-tenant"))
        {
            grant.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = companyA.Id,
                UserId = auditor.Id,
                Capabilities = CompanyCapability.View,
                IsActive = true
            });
            await grant.SaveChangesAsync();
        }

        var locationIds = new[] { locationA.Id, locationB.Id };
        List<StockBalanceSnapshot> balancesBefore;
        await using (var before = fixture.CreateContext("test-tenant"))
        {
            balancesBefore = await before.StockInHand
                .AsNoTracking()
                .Where(stock => locationIds.Contains(stock.LocationId))
                .OrderBy(stock => stock.LocationId)
                .ThenBy(stock => stock.ItemId)
                .ThenBy(stock => stock.BatchNumber)
                .ThenBy(stock => stock.ExpiryDate)
                .Select(stock => new StockBalanceSnapshot(
                    stock.ItemId,
                    stock.LocationId,
                    stock.BatchNumber,
                    stock.ExpiryDate,
                    stock.Quantity,
                    stock.ReservedQuantity,
                    stock.QuarantinedQuantity))
                .ToListAsync();
        }
        balancesBefore.Should().HaveCount(5, "all requested and decoy balances must be persisted before the API calls");

        using var client = _factory.CreateAuthenticatedClient(auditor, "RestrictedAuditor");
        var filteredQuery = $"locationId={locationA.Id}"
            + $"&companyId={companyA.Id}"
            + $"&itemId={requestedItem.Id}"
            + $"&batchNumber={Uri.EscapeDataString(requestedLot)}"
            + $"&expiryDate={Uri.EscapeDataString(requestedExpiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}";

        using var authorizedResponse = await client.GetAsync(
            $"/api/v1/stock/counts/reconciliation?{filteredQuery}");
        authorizedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var authorizedBody = await authorizedResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            var data = authorizedBody!.RootElement.GetProperty("data");
            data.GetProperty("locationId").GetInt32().Should().Be(locationA.Id);
            data.GetProperty("companyId").GetInt32().Should().Be(companyA.Id);

            var result = data.GetProperty("positions").EnumerateArray().Should().ContainSingle().Subject;
            result.GetProperty("itemId").GetInt32().Should().Be(requestedItem.Id);
            result.GetProperty("itemCode").GetString().Should().Be(requestedItem.ItemCode);
            result.GetProperty("batchNumber").GetString().Should().Be(requestedLot);
            result.GetProperty("expiryDate").GetDateTime().Date.Should().Be(requestedExpiry.Date);
            result.GetProperty("onHandQuantity").GetInt32().Should().Be(11);
            result.GetProperty("reservedQuantity").GetInt32().Should().Be(2);
            result.GetProperty("quarantinedQuantity").GetInt32().Should().Be(1);
            result.GetProperty("availableQuantity").GetInt32().Should().Be(8);
            var ledger = result.GetProperty("ledger").EnumerateArray().Should().ContainSingle().Subject;
            ledger.GetProperty("quantityDelta").GetInt32().Should().Be(11);
        }

        using var restrictedResponse = await client.GetAsync(
            $"/api/v1/stock/counts/reconciliation?locationId={locationB.Id}&companyId={companyB.Id}");
        restrictedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the same signed JWT has no View grant for company B's persisted location");

        var forgedCompanyQuery = filteredQuery.Replace(
            $"companyId={companyA.Id}",
            $"companyId={companyB.Id}",
            StringComparison.Ordinal);
        using var forgedCompanyResponse = await client.GetAsync(
            $"/api/v1/stock/counts/reconciliation?{forgedCompanyQuery}");
        forgedCompanyResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a caller-supplied company ID must not override the company owning an accessible location");
        using (var forgedCompanyBody = await forgedCompanyResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            var root = forgedCompanyBody!.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("errorMessage").GetString().Should().Be("Stock reconciliation scope not found.");
        }

        await using var after = fixture.CreateContext("test-tenant");
        var balancesAfter = await after.StockInHand
            .AsNoTracking()
            .Where(stock => locationIds.Contains(stock.LocationId))
            .OrderBy(stock => stock.LocationId)
            .ThenBy(stock => stock.ItemId)
            .ThenBy(stock => stock.BatchNumber)
            .ThenBy(stock => stock.ExpiryDate)
            .Select(stock => new StockBalanceSnapshot(
                stock.ItemId,
                stock.LocationId,
                stock.BatchNumber,
                stock.ExpiryDate,
                stock.Quantity,
                stock.ReservedQuantity,
                stock.QuarantinedQuantity))
            .ToListAsync();
        balancesAfter.Should().BeEquivalentTo(balancesBefore,
            "authenticated reconciliation reads and denied requests must not mutate seeded balances");
    }

    private static (StockInHand Balance, StockTransaction Movement) CreatePosition(
        Item item,
        Location location,
        int quantity,
        string batchNumber,
        DateTime expiryDate,
        int reservedQuantity = 0,
        int quarantinedQuantity = 0)
    {
        var movement = new StockTransaction
        {
            Item = item,
            FromLocation = location,
            ToLocation = location,
            Quantity = quantity,
            TransactionType = TransactionType.Receive,
            TransactionDate = DateTime.UtcNow.AddMinutes(-1),
            BatchNumber = batchNumber,
            ExpiryDate = expiryDate,
            Notes = "Synthetic reconciliation authorization fixture"
        };
        return (
            new StockInHand
            {
                Item = item,
                Location = location,
                Quantity = quantity,
                ReservedQuantity = reservedQuantity,
                QuarantinedQuantity = quarantinedQuantity,
                BatchNumber = batchNumber,
                ExpiryDate = expiryDate
            },
            movement);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record StockBalanceSnapshot(
        int ItemId,
        int LocationId,
        string? BatchNumber,
        DateTime? ExpiryDate,
        int Quantity,
        int ReservedQuantity,
        int QuarantinedQuantity);
}
