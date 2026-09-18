using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TransferAgingReconciliationPostgreSqlApiTests(
    PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture,
        "merconiq-transfer-aging-api");
    private readonly PostgreSqlCompanyApiFactory _otherTenantFactory = new(
        fixture,
        "merconiq-transfer-aging-other-tenant-api",
        "transfer-aging-other-tenant");

    [PostgreSqlFact]
    public async Task Authenticated_report_reconciles_transit_and_preserves_company_tenant_and_closed_order_scope()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var company = await SeedCompanyAsync(_factory, fixture, "test-tenant", $"A{suffix}", 4);
        var otherCompany = await SeedCompanyAsync(_factory, fixture, "test-tenant", $"B{suffix}", 1);
        var otherTenantCompany = await SeedCompanyAsync(
            _otherTenantFactory, fixture, "transfer-aging-other-tenant", $"T{suffix}", 1);

        var user = await _factory.EnsurePersonaUserAsync("Manager", suffix);
        await using (var grantContext = fixture.CreateContext("test-tenant"))
        {
            grantContext.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = company.CompanyId,
                UserId = user.Id,
                Capabilities = CompanyCapability.View,
                IsActive = true
            });
            await grantContext.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(user, "Manager");

        var partialOrder = await CreateOrderAsync(
            _factory,
            company,
            [
                new TransferOrderLineRequest(company.ItemIds[0], 10),
                new TransferOrderLineRequest(company.ItemIds[1], 5)
            ],
            $"aging-partial-{suffix}",
            approve: true);
        var partialLine = partialOrder.Lines.Single(line => line.ItemId == company.ItemIds[0]);
        var reserveOnlyLine = partialOrder.Lines.Single(line => line.ItemId == company.ItemIds[1]);
        TransferDispatchView dispatch;
        await using (var operation = _factory.Services.CreateAsyncScope())
        {
            var transferOrders = operation.ServiceProvider.GetRequiredService<ITransferOrderService>();
            var scope = new StockMutationScope(company.CompanyId, _ => Task.FromResult(true));
            dispatch = await transferOrders.DispatchAsync(
                partialOrder.Id,
                partialLine.Id,
                10,
                $"aging-dispatch-{suffix}",
                "dispatcher-42",
                scope);
            await transferOrders.ResolveTransitAsync(
                partialOrder.Id,
                partialLine.Id,
                dispatch.Id,
                new TransferTransitSettlementRequest(3, TransferTransitSettlementType.Received),
                $"aging-receive-{suffix}",
                "receiver-17",
                scope);
            await transferOrders.ResolveTransitAsync(
                partialOrder.Id,
                partialLine.Id,
                dispatch.Id,
                new TransferTransitSettlementRequest(
                    2,
                    TransferTransitSettlementType.Quarantined,
                    Reason: "Synthetic inspection hold"),
                $"aging-quarantine-{suffix}",
                "quality-operator-5",
                scope);
            await transferOrders.ResolveTransitAsync(
                partialOrder.Id,
                partialLine.Id,
                dispatch.Id,
                new TransferTransitSettlementRequest(1, TransferTransitSettlementType.Returned),
                $"aging-return-{suffix}",
                "return-operator-9",
                scope);
        }

        var completedOrder = await CreateOrderAsync(
            _factory,
            company,
            [new TransferOrderLineRequest(company.ItemIds[2], 2)],
            $"aging-completed-{suffix}",
            approve: true);
        var completedLine = completedOrder.Lines.Single();
        await using (var operation = _factory.Services.CreateAsyncScope())
        {
            var transferOrders = operation.ServiceProvider.GetRequiredService<ITransferOrderService>();
            var scope = new StockMutationScope(company.CompanyId, _ => Task.FromResult(true));
            var completedDispatch = await transferOrders.DispatchAsync(
                completedOrder.Id,
                completedLine.Id,
                2,
                $"aging-completed-dispatch-{suffix}",
                "dispatcher-42",
                scope);
            await transferOrders.ResolveTransitAsync(
                completedOrder.Id,
                completedLine.Id,
                completedDispatch.Id,
                new TransferTransitSettlementRequest(2, TransferTransitSettlementType.Received),
                $"aging-completed-receive-{suffix}",
                "receiver-17",
                scope);
        }

        var cancelledOrder = await CreateOrderAsync(
            _factory,
            company,
            [new TransferOrderLineRequest(company.ItemIds[3], 1)],
            $"aging-cancelled-{suffix}",
            approve: true);
        await using (var operation = _factory.Services.CreateAsyncScope())
        {
            var transferOrders = operation.ServiceProvider.GetRequiredService<ITransferOrderService>();
            await transferOrders.CancelAsync(
                cancelledOrder.Id,
                new StockMutationScope(company.CompanyId));
        }

        var otherCompanyOrder = await CreateOrderAsync(
            _factory,
            otherCompany,
            [new TransferOrderLineRequest(otherCompany.ItemIds[0], 1)],
            $"aging-other-company-{suffix}",
            approve: false);
        var otherTenantOrder = await CreateOrderAsync(
            _otherTenantFactory,
            otherTenantCompany,
            [new TransferOrderLineRequest(otherTenantCompany.ItemIds[0], 1)],
            $"aging-other-tenant-{suffix}",
            approve: false);

        using var response = await client.GetAsync("/api/v1/transfer-orders/aging?pageSize=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<TransferAgingReconciliationPage>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        var page = envelope.Data!;
        page.Lines.Should().HaveCount(4);
        page.Lines.Should().OnlyContain(line => line.CompanyId == company.CompanyId);
        page.Lines.Select(line => line.TransferOrderId)
            .Should().NotContain(otherCompanyOrder.Id, "another company order must not leak")
            .And.NotContain(otherTenantOrder.Id, "another tenant order must not leak");

        var partial = page.Lines.Single(line => line.TransferOrderLineId == partialLine.Id);
        partial.TransferOrderStatus.Should().Be(TransferOrderStatus.PartiallyReceived);
        partial.OrderedQuantity.Should().Be(10);
        partial.ReservedQuantity.Should().Be(0);
        partial.DispatchedQuantity.Should().Be(10);
        partial.DispatchedLineCounterVariance.Should().Be(0);
        partial.ReceivedQuantity.Should().Be(3);
        partial.QuarantinedQuantity.Should().Be(2);
        partial.ReturnedQuantity.Should().Be(1);
        partial.OutstandingTransitQuantity.Should().Be(4);
        partial.QuantityConservationVariance.Should().Be(0);
        partial.OriginalAverageUnitCost.Should().Be(12.5m);
        partial.OutstandingAverageUnitCost.Should().Be(12.5m);
        partial.DispatchedValue.Should().Be(125m);
        partial.ReceivedValue.Should().Be(37.5m);
        partial.QuarantinedValue.Should().Be(25m);
        partial.ReturnedValue.Should().Be(12.5m);
        partial.OutstandingTransitValue.Should().Be(50m);
        partial.ValueConservationVariance.Should().Be(0m);
        partial.DispatchLedgerQuantityVariance.Should().Be(0);
        partial.DispatchLedgerValueVariance.Should().Be(0m);
        partial.SettlementLedgerQuantityVariance.Should().Be(0);
        partial.SettlementLedgerValueVariance.Should().Be(0m);
        partial.OldestOutstandingDispatchedAt.Should().Be(dispatch.DispatchedAt);
        partial.OldestOutstandingAgeDays.Should().Be(
            Math.Max(0, (int)(page.AsOf - dispatch.DispatchedAt).TotalDays));
        partial.LatestTransitAction.Should().Be(nameof(TransferTransitSettlementType.Returned));
        partial.LatestTransitActionBy.Should().Be("return-operator-9");

        var reserveOnly = page.Lines.Single(line => line.TransferOrderLineId == reserveOnlyLine.Id);
        reserveOnly.ReservedQuantity.Should().Be(5);
        reserveOnly.DispatchedQuantity.Should().Be(0);
        reserveOnly.OutstandingTransitQuantity.Should().Be(0);

        page.Lines.Single(line => line.TransferOrderId == completedOrder.Id)
            .TransferOrderStatus.Should().Be(TransferOrderStatus.Completed);
        page.Lines.Single(line => line.TransferOrderId == cancelledOrder.Id)
            .TransferOrderStatus.Should().Be(TransferOrderStatus.Cancelled);

        using var deniedCompanyResponse = await client.GetAsync(
            $"/api/v1/transfer-orders/aging?companyId={otherCompany.CompanyId}");
        deniedCompanyResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var deniedTenantResponse = await client.GetAsync(
            $"/api/v1/transfer-orders/aging?companyId={otherTenantCompany.CompanyId}");
        deniedTenantResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var firstPageResponse = await client.GetAsync("/api/v1/transfer-orders/aging?pageSize=2");
        var firstPageEnvelope = await firstPageResponse.Content
            .ReadFromJsonAsync<ApiResponse<TransferAgingReconciliationPage>>();
        firstPageEnvelope!.Data!.Lines.Should().HaveCount(2);
        firstPageEnvelope.Data.NextAfterLineId.Should().NotBeNull();
        using var secondPageResponse = await client.GetAsync(
            $"/api/v1/transfer-orders/aging?pageSize=2&afterLineId={firstPageEnvelope.Data.NextAfterLineId}");
        var secondPageEnvelope = await secondPageResponse.Content
            .ReadFromJsonAsync<ApiResponse<TransferAgingReconciliationPage>>();
        secondPageEnvelope!.Data!.Lines.Should().HaveCount(2);
        secondPageEnvelope.Data.NextAfterLineId.Should().BeNull();
        firstPageEnvelope.Data.Lines.Concat(secondPageEnvelope.Data.Lines)
            .Select(line => line.TransferOrderLineId)
            .Should().BeEquivalentTo(page.Lines.Select(line => line.TransferOrderLineId));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _otherTenantFactory.Dispose();
    }

    private static async Task<CompanyScenario> SeedCompanyAsync(
        PostgreSqlCompanyApiFactory factory,
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string suffix,
        int itemCount)
    {
        int companyId;
        int sourceLocationId;
        int destinationLocationId;
        int[] itemIds;
        await using (var context = fixture.CreateContext(tenantId))
        {
            var company = new Company
            {
                Code = $"TA-{suffix[..10]}",
                LegalName = "Synthetic transfer aging company",
                BaseCurrency = "USD"
            };
            var sourceBranch = new Branch
            {
                Company = company,
                Code = $"TAS-{suffix[..8]}",
                Name = "Synthetic source branch"
            };
            var destinationBranch = new Branch
            {
                Company = company,
                Code = $"TAD-{suffix[..8]}",
                Name = "Synthetic destination branch"
            };
            var source = new Location { Branch = sourceBranch, Name = $"Synthetic source {suffix[..8]}" };
            var destination = new Location
            {
                Branch = destinationBranch,
                Name = $"Synthetic destination {suffix[..8]}"
            };
            var items = Enumerable.Range(0, itemCount)
                .Select(index => new Item
                {
                    ItemCode = $"TA-{suffix[..8]}-{index}",
                    Description = $"Synthetic transfer aging item {index}",
                    Rate = 1m
                })
                .ToArray();
            context.AddRange(company, sourceBranch, destinationBranch, source, destination);
            context.Items.AddRange(items);
            await context.SaveChangesAsync();
            companyId = company.Id;
            sourceLocationId = source.Id;
            destinationLocationId = destination.Id;
            itemIds = items.Select(item => item.Id).ToArray();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stock = scope.ServiceProvider.GetRequiredService<IStockService>();
            for (var index = 0; index < itemIds.Length; index++)
            {
                var unitCost = index switch
                {
                    0 => 12.5m,
                    1 => 8m,
                    2 => 5m,
                    _ => 7m
                };
                await stock.ReceiveStockAsync(
                    itemIds[index],
                    sourceLocationId,
                    100,
                    "Synthetic transfer aging report fixture",
                    unitCost: unitCost,
                    mutationScope: new StockMutationScope(companyId));
            }
        }

        return new CompanyScenario(companyId, sourceLocationId, destinationLocationId, itemIds);
    }

    private static async Task<TransferOrderView> CreateOrderAsync(
        PostgreSqlCompanyApiFactory factory,
        CompanyScenario company,
        IReadOnlyCollection<TransferOrderLineRequest> lines,
        string idempotencyKey,
        bool approve)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var transferOrders = scope.ServiceProvider.GetRequiredService<ITransferOrderService>();
        var order = await transferOrders.CreateAsync(
            new CreateTransferOrderRequest(
                company.CompanyId,
                company.SourceLocationId,
                company.DestinationLocationId,
                lines),
            idempotencyKey);
        if (approve)
            await transferOrders.ApproveAsync(order.Id, new StockMutationScope(company.CompanyId));
        return order;
    }

    private sealed record CompanyScenario(
        int CompanyId,
        int SourceLocationId,
        int DestinationLocationId,
        int[] ItemIds);
}
