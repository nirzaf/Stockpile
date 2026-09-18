using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockReturnCompanyAuthorizationPostgreSqlApiTests(
    PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture, applicationName: "merconiq-stock-return-company-authorization-api");

    [PostgreSqlFact]
    public async Task Return_api_hides_foreign_company_movements_and_preserves_their_stock()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        int authorizedCompanyId;
        int authorizedLocationId;
        int authorizedSaleId;
        int foreignCompanyId;
        int foreignLocationId;
        int foreignSaleId;
        int foreignReceiptId;
        int itemId;

        await using (var seed = fixture.CreateContext("test-tenant"))
        {
            var authorizedCompany = new Company
            {
                Code = $"RET-A-{suffix[..10]}",
                LegalName = "Synthetic authorized return company",
                BaseCurrency = "USD"
            };
            var foreignCompany = new Company
            {
                Code = $"RET-B-{suffix[..10]}",
                LegalName = "Synthetic foreign return company",
                BaseCurrency = "USD"
            };
            var authorizedBranch = new Branch
            {
                Company = authorizedCompany,
                Code = $"RA-{suffix[..8]}",
                Name = "Authorized return branch"
            };
            var foreignBranch = new Branch
            {
                Company = foreignCompany,
                Code = $"RB-{suffix[..8]}",
                Name = "Foreign return branch"
            };
            var authorizedLocation = new Location
            {
                Branch = authorizedBranch,
                Name = $"Authorized return location {suffix[..8]}"
            };
            var foreignLocation = new Location
            {
                Branch = foreignBranch,
                Name = $"Foreign return location {suffix[..8]}"
            };
            var item = new Item
            {
                ItemCode = $"RET-ITEM-{suffix[..10]}",
                Description = "Synthetic company-scoped return authorization item",
                Rate = 1m
            };
            var authorizedSale = new StockTransaction
            {
                Item = item,
                FromLocation = authorizedLocation,
                Quantity = 2,
                TransactionType = TransactionType.Sell,
                TransactionDate = DateTime.UtcNow
            };
            var foreignSale = new StockTransaction
            {
                Item = item,
                FromLocation = foreignLocation,
                Quantity = 2,
                TransactionType = TransactionType.Sell,
                TransactionDate = DateTime.UtcNow
            };
            var foreignReceipt = new StockTransaction
            {
                Item = item,
                FromLocation = foreignLocation,
                Quantity = 2,
                TransactionType = TransactionType.Receive,
                TransactionDate = DateTime.UtcNow
            };

            seed.AddRange(
                authorizedCompany,
                foreignCompany,
                authorizedBranch,
                foreignBranch,
                authorizedLocation,
                foreignLocation,
                item,
                new StockInHand { Item = item, Location = authorizedLocation, Quantity = 4 },
                new StockInHand { Item = item, Location = foreignLocation, Quantity = 4 },
                authorizedSale,
                foreignSale,
                foreignReceipt);
            await seed.SaveChangesAsync();

            authorizedCompanyId = authorizedCompany.Id;
            authorizedLocationId = authorizedLocation.Id;
            authorizedSaleId = authorizedSale.Id;
            foreignCompanyId = foreignCompany.Id;
            foreignLocationId = foreignLocation.Id;
            foreignSaleId = foreignSale.Id;
            foreignReceiptId = foreignReceipt.Id;
            itemId = item.Id;
        }

        var operatorUser = await _factory.EnsurePersonaUserAsync("Operator", suffix);
        await using (var grant = fixture.CreateContext("test-tenant"))
        {
            grant.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = authorizedCompanyId,
                UserId = operatorUser.Id,
                Capabilities = CompanyCapability.Post,
                IsActive = true
            });
            await grant.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(operatorUser, "Operator");
        using var authorizedResponse = await client.PostAsJsonAsync(
            "/api/v1/stock/returns",
            new CreateStockReturnRequest(
                authorizedSaleId,
                1,
                StockReturnDisposition.Restockable,
                $"authorized-return-{suffix}"));
        authorizedResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the same authenticated operator must be able to return a sale in its granted company");

        using var foreignResponse = await client.PostAsJsonAsync(
            "/api/v1/stock/returns",
            new CreateStockReturnRequest(
                foreignSaleId,
                1,
                StockReturnDisposition.Restockable,
                $"foreign-return-{suffix}"));
        using var foreignNonSaleResponse = await client.PostAsJsonAsync(
            "/api/v1/stock/returns",
            new CreateStockReturnRequest(
                foreignReceiptId,
                1,
                StockReturnDisposition.Restockable,
                $"foreign-nonsale-return-{suffix}"));
        using var missingResponse = await client.PostAsJsonAsync(
            "/api/v1/stock/returns",
            new CreateStockReturnRequest(
                int.MaxValue,
                1,
                StockReturnDisposition.Restockable,
                $"missing-return-{suffix}"));

        foreignResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an inaccessible company's sale must not be distinguishable from a missing movement");
        foreignNonSaleResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an inaccessible non-sale movement must not reveal its type through the return validation response");
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var missingBody = await missingResponse.Content.ReadAsStringAsync();
        (await foreignResponse.Content.ReadAsStringAsync()).Should().Be(missingBody,
            "an inaccessible sale must have the same response body as a missing movement");
        (await foreignNonSaleResponse.Content.ReadAsStringAsync()).Should().Be(missingBody,
            "an inaccessible non-sale movement must have the same response body as a missing movement");

        await using var verify = fixture.CreateContext("test-tenant");
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.Return &&
            transaction.OriginalTransactionId == authorizedSaleId)).Should().Be(1);
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.Return &&
            transaction.OriginalTransactionId == foreignSaleId)).Should().Be(0,
            "the denied request must not append a return movement to the foreign company's ledger");
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.Return &&
            transaction.OriginalTransactionId == foreignReceiptId)).Should().Be(0);
        (await verify.StockInHand.SingleAsync(stock =>
            stock.ItemId == itemId && stock.LocationId == authorizedLocationId)).Quantity.Should().Be(5);
        (await verify.StockInHand.SingleAsync(stock =>
            stock.ItemId == itemId && stock.LocationId == foreignLocationId)).Quantity.Should().Be(4);
        foreignCompanyId.Should().NotBe(authorizedCompanyId,
            "the persisted targets must belong to distinct companies in the same tenant");
        (await verify.StockTransactions.CountAsync(transaction => transaction.Id == foreignSaleId)).Should().Be(1,
            "the forbidden target is a real persisted sale, not a missing-row control");
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.Id == foreignReceiptId && transaction.TransactionType == TransactionType.Receive)).Should().Be(1,
            "the other forbidden target is a real non-sale movement whose type was previously exposed");
        (await verify.Locations
            .Where(location => location.Id == authorizedLocationId || location.Id == foreignLocationId)
            .Select(location => new { location.Id, CompanyId = location.Branch!.CompanyId })
            .ToDictionaryAsync(location => location.Id, location => location.CompanyId))
            .Should().BeEquivalentTo(new Dictionary<int, int>
            {
                [authorizedLocationId] = authorizedCompanyId,
                [foreignLocationId] = foreignCompanyId
            });
    }

    public void Dispose() => _factory.Dispose();
}
