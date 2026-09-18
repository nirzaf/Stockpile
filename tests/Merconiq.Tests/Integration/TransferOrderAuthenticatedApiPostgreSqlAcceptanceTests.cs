using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TransferOrderAuthenticatedApiPostgreSqlAcceptanceTests(
    PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture,
        "merconiq-transfer-order-api-acceptance");

    [PostgreSqlFact]
    public async Task Authenticated_operators_can_create_amend_add_remove_approve_and_cancel_without_stock_movement()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var scenario = await SeedScenarioAsync(fixture, suffix);
        var manager = await CreatePersonaAsync(
            "Manager",
            CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Post,
            suffix,
            scenario.CompanyId);
        var accountant = await CreatePersonaAsync(
            "Accountant",
            CompanyCapability.View | CompanyCapability.Approve | CompanyCapability.Post,
            suffix,
            scenario.CompanyId);
        using var managerClient = manager.Client;
        using var accountantClient = accountant.Client;

        var createRequest = new CreateTransferOrderRequest(
            scenario.CompanyId,
            scenario.SourceLocationId,
            scenario.DestinationLocationId,
            [
                new TransferOrderLineRequest(scenario.RetainedItemId, 2),
                new TransferOrderLineRequest(scenario.RemovedItemId, 3),
                new TransferOrderLineRequest(scenario.OtherRetainedItemId, 4)
            ],
            "Synthetic authenticated API acceptance scenario");
        using var createMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfer-orders")
        {
            Content = JsonContent.Create(createRequest)
        };
        createMessage.Headers.Add("Idempotency-Key", $"transfer-acceptance-{suffix}");
        using var createResponse = await managerClient.SendAsync(createMessage);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ApiResponse<TransferOrderView>>();
        created.Should().NotBeNull();
        created!.Success.Should().BeTrue();
        created.Data.Should().NotBeNull();
        var createdOrder = created.Data!;
        createdOrder.Status.Should().Be(TransferOrderStatus.Draft);
        createdOrder.Lines.Should().HaveCount(3);
        var originalLineIds = createdOrder.Lines.ToDictionary(line => line.ItemId, line => line.Id);

        await AssertPersistedStateAsync(
            fixture,
            scenario,
            createdOrder.Id,
            TransferOrderStatus.Draft,
            expectedReservations: []);

        var amendment = new CreateTransferOrderRequest(
            scenario.CompanyId,
            scenario.SourceLocationId,
            scenario.DestinationLocationId,
            [
                new TransferOrderLineRequest(
                    scenario.RetainedItemId,
                    2,
                    LineId: originalLineIds[scenario.RetainedItemId]),
                new TransferOrderLineRequest(
                    scenario.OtherRetainedItemId,
                    4,
                    LineId: originalLineIds[scenario.OtherRetainedItemId]),
                new TransferOrderLineRequest(scenario.AddedItemId, 5)
            ],
            "Synthetic amendment: remove one line and add another");
        using var amendResponse = await managerClient.PutAsJsonAsync(
            $"/api/v1/transfer-orders/{createdOrder.Id}",
            amendment);

        amendResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var amendedResponse = await managerClient.GetAsync($"/api/v1/transfer-orders/{createdOrder.Id}");
        amendedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var amended = await amendedResponse.Content.ReadFromJsonAsync<ApiResponse<TransferOrderView>>();
        amended.Should().NotBeNull();
        amended!.Success.Should().BeTrue();
        amended.Data.Should().NotBeNull();
        var amendedOrder = amended.Data!;
        amendedOrder.Status.Should().Be(TransferOrderStatus.Draft);
        amendedOrder.DocumentId.Should().Be(createdOrder.DocumentId);
        amendedOrder.Number.Should().Be(createdOrder.Number);
        amendedOrder.Lines.Should().HaveCount(3);
        amendedOrder.Lines.Should().Contain(line =>
            line.ItemId == scenario.RetainedItemId &&
            line.Id == originalLineIds[scenario.RetainedItemId] &&
            line.Quantity == 2);
        amendedOrder.Lines.Should().Contain(line =>
            line.ItemId == scenario.OtherRetainedItemId &&
            line.Id == originalLineIds[scenario.OtherRetainedItemId] &&
            line.Quantity == 4);
        amendedOrder.Lines.Should().NotContain(line => line.Id == originalLineIds[scenario.RemovedItemId]);
        var addedLine = amendedOrder.Lines.Should()
            .ContainSingle(line => line.ItemId == scenario.AddedItemId)
            .Which;
        originalLineIds.Values.Should().NotContain(addedLine.Id);
        addedLine.Quantity.Should().Be(5);

        var expectedReservations = new[]
        {
            (scenario.RetainedItemId, 2),
            (scenario.OtherRetainedItemId, 4),
            (scenario.AddedItemId, 5)
        };
        await AssertPersistedStateAsync(
            fixture,
            scenario,
            createdOrder.Id,
            TransferOrderStatus.Draft,
            expectedReservations: []);

        using var approveResponse = await accountantClient.PostAsync(
            $"/api/v1/transfer-orders/{createdOrder.Id}/approve",
            content: null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertPersistedStateAsync(
            fixture,
            scenario,
            createdOrder.Id,
            TransferOrderStatus.Approved,
            expectedReservations);

        using var cancelResponse = await managerClient.PostAsync(
            $"/api/v1/transfer-orders/{createdOrder.Id}/cancel",
            content: null);
        cancelResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertPersistedStateAsync(
            fixture,
            scenario,
            createdOrder.Id,
            TransferOrderStatus.Cancelled,
            expectedReservations);
    }

    private async Task<(ApplicationUser User, HttpClient Client)> CreatePersonaAsync(
        string role,
        CompanyCapability capabilities,
        string suffix,
        int companyId)
    {
        var user = await _factory.EnsurePersonaUserAsync(role, suffix);
        await using (var context = fixture.CreateContext("test-tenant"))
        {
            context.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = companyId,
                UserId = user.Id,
                Capabilities = capabilities,
                IsActive = true
            });
            await context.SaveChangesAsync();
        }

        return (user, _factory.CreateAuthenticatedClient(user, role));
    }

    private static async Task<TransferOrderScenario> SeedScenarioAsync(
        PostgreSqlIntegrationFixture fixture,
        string suffix)
    {
        await using var context = fixture.CreateContext("test-tenant");
        var company = new Company
        {
            Code = $"TOA-{suffix[..10]}",
            LegalName = "Synthetic transfer-order acceptance company",
            BaseCurrency = "USD"
        };
        var branch = new Branch { Company = company, Code = $"TOA-{suffix[..8]}", Name = "Synthetic branch" };
        var source = new Location { Branch = branch, Name = $"Synthetic source {suffix[..8]}" };
        var destination = new Location { Branch = branch, Name = $"Synthetic destination {suffix[..8]}" };
        var items = Enumerable.Range(0, 4)
            .Select(index => new Item
            {
                ItemCode = $"TOA-{suffix[..10]}-{index}",
                Description = $"Synthetic transfer item {index}",
                Rate = 1m
            })
            .ToArray();

        context.AddRange(company, branch, source, destination);
        context.Items.AddRange(items);
        await context.SaveChangesAsync();

        var sourceQuantities = new[] { 20, 21, 22, 23 };
        var destinationQuantities = new[] { 5, 6, 7, 8 };
        for (var index = 0; index < items.Length; index++)
        {
            context.StockInHand.AddRange(
                new StockInHand
                {
                    ItemId = items[index].Id,
                    LocationId = source.Id,
                    Quantity = sourceQuantities[index]
                },
                new StockInHand
                {
                    ItemId = items[index].Id,
                    LocationId = destination.Id,
                    Quantity = destinationQuantities[index]
                });
        }

        await context.SaveChangesAsync();
        return new TransferOrderScenario(
            "test-tenant",
            company.Id,
            source.Id,
            destination.Id,
            items[0].Id,
            items[1].Id,
            items[2].Id,
            items[3].Id,
            items.Select(item => item.Id).ToArray(),
            sourceQuantities,
            destinationQuantities);
    }

    private static async Task AssertPersistedStateAsync(
        PostgreSqlIntegrationFixture fixture,
        TransferOrderScenario scenario,
        int orderId,
        TransferOrderStatus expectedStatus,
        IReadOnlyCollection<(int ItemId, int Quantity)> expectedReservations)
    {
        await using var verify = fixture.CreateContext(scenario.TenantId);
        var order = await verify.TransferOrders.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == orderId);
        order.Status.Should().Be(expectedStatus);

        var stock = await verify.StockInHand.AsNoTracking()
            .Where(row => scenario.ItemIds.Contains(row.ItemId) &&
                          (row.LocationId == scenario.SourceLocationId ||
                           row.LocationId == scenario.DestinationLocationId))
            .ToListAsync();
        stock.Should().HaveCount(scenario.ItemIds.Length * 2);
        for (var index = 0; index < scenario.ItemIds.Length; index++)
        {
            var itemId = scenario.ItemIds[index];
            stock.Single(row => row.ItemId == itemId && row.LocationId == scenario.SourceLocationId)
                .Quantity.Should().Be(scenario.SourceQuantities[index]);
            stock.Single(row => row.ItemId == itemId && row.LocationId == scenario.DestinationLocationId)
                .Quantity.Should().Be(scenario.DestinationQuantities[index]);
        }

        (await verify.StockTransactions.AsNoTracking()
            .CountAsync(transaction => scenario.ItemIds.Contains(transaction.ItemId)))
            .Should().Be(0, "reservation and cancellation do not move stock; dispatch is outside this scenario");

        var reservations = await verify.StockReservations.AsNoTracking()
            .Where(reservation => scenario.ItemIds.Contains(reservation.ItemId))
            .ToListAsync();
        if (expectedStatus == TransferOrderStatus.Draft)
        {
            reservations.Should().BeEmpty();
            stock.Where(row => row.LocationId == scenario.SourceLocationId)
                .Should().OnlyContain(row => row.ReservedQuantity == 0);
            return;
        }

        reservations.Select(reservation => (reservation.ItemId, reservation.Quantity))
            .Should().BeEquivalentTo(expectedReservations);
        var expectedReservationStatus = expectedStatus == TransferOrderStatus.Approved
            ? StockReservationStatus.Active
            : StockReservationStatus.Released;
        reservations.Should().OnlyContain(reservation => reservation.Status == expectedReservationStatus);
        stock.Where(row => row.LocationId == scenario.SourceLocationId)
            .Should().OnlyContain(row => row.ReservedQuantity ==
                (expectedStatus == TransferOrderStatus.Approved
                    ? expectedReservations.SingleOrDefault(expected => expected.ItemId == row.ItemId).Quantity
                    : 0));
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TransferOrderScenario(
        string TenantId,
        int CompanyId,
        int SourceLocationId,
        int DestinationLocationId,
        int RetainedItemId,
        int RemovedItemId,
        int OtherRetainedItemId,
        int AddedItemId,
        int[] ItemIds,
        int[] SourceQuantities,
        int[] DestinationQuantities);

}
