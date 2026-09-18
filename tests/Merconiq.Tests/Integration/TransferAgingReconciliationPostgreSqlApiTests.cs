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
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "report response was: {0}",
            await response.Content.ReadAsStringAsync());
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
        partial.DispatchValuationPostingCountVariance.Should().Be(0);
        partial.DispatchValuationQuantityVariance.Should().Be(0);
        partial.SettlementLedgerQuantityVariance.Should().Be(0);
        partial.SettlementLedgerValueVariance.Should().Be(0m);
        partial.SettlementValuationPostingCountVariance.Should().Be(0);
        partial.SettlementValuationQuantityVariance.Should().Be(0);
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

    [PostgreSqlFact]
    public async Task Approver_can_write_off_transit_through_idempotent_api_and_report_without_stock_movement()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var company = await SeedCompanyAsync(_factory, fixture, "test-tenant", $"W{suffix}", 1);
        var manager = await _factory.EnsurePersonaUserAsync("Manager", suffix);
        var accountant = await _factory.EnsurePersonaUserAsync("Accountant", suffix);
        await using (var grantContext = fixture.CreateContext("test-tenant"))
        {
            grantContext.CompanyMemberships.AddRange(
                new CompanyMembership
                {
                    CompanyId = company.CompanyId,
                    UserId = manager.Id,
                    Capabilities = CompanyCapability.View | CompanyCapability.Post,
                    IsActive = true
                },
                new CompanyMembership
                {
                    CompanyId = company.CompanyId,
                    UserId = accountant.Id,
                    Capabilities = CompanyCapability.View | CompanyCapability.Approve,
                    IsActive = true
                });
            await grantContext.SaveChangesAsync();
        }

        var order = await CreateOrderAsync(
            _factory,
            company,
            [new TransferOrderLineRequest(company.ItemIds[0], 10)],
            $"write-off-order-{suffix}",
            approve: true);
        var line = order.Lines.Single();
        TransferDispatchView dispatch;
        await using (var operation = _factory.Services.CreateAsyncScope())
        {
            var transferOrders = operation.ServiceProvider.GetRequiredService<ITransferOrderService>();
            dispatch = await transferOrders.DispatchAsync(
                order.Id,
                line.Id,
                10,
                $"write-off-dispatch-{suffix}",
                "dispatcher-42",
                new StockMutationScope(company.CompanyId, _ => Task.FromResult(true)));
        }

        await using var snapshotContext = fixture.CreateContext("test-tenant");
        var transactionCountBefore = await snapshotContext.StockTransactions.CountAsync(transaction =>
            transaction.ItemId == company.ItemIds[0]);
        var valuationEntryCountBefore = await snapshotContext.StockValuationEntries.CountAsync(entry =>
            entry.ItemId == company.ItemIds[0]);
        var stockBefore = await snapshotContext.StockInHand.AsNoTracking()
            .Where(stock => stock.ItemId == company.ItemIds[0])
            .OrderBy(stock => stock.LocationId)
            .Select(stock => new { stock.LocationId, stock.Quantity, stock.ReservedQuantity, stock.QuarantinedQuantity })
            .ToListAsync();
        var valuationBucketsBefore = await snapshotContext.StockValuationBuckets.AsNoTracking()
            .Where(bucket => bucket.ItemId == company.ItemIds[0])
            .OrderBy(bucket => bucket.LocationId)
            .Select(bucket => new { bucket.LocationId, bucket.Quantity, bucket.Value })
            .ToListAsync();

        var route = $"/api/v1/transfer-orders/{order.Id}/lines/{line.Id}/transit/{dispatch.Id}/write-off";
        static HttpRequestMessage CreateWriteOffRequest(string path, string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new TransferTransitSettlementRequest(
                    4,
                    TransferTransitSettlementType.WrittenOff,
                    Reason: "Lost during carrier handoff"))
            };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }

        using var managerClient = _factory.CreateAuthenticatedClient(manager, "Manager");
        using (var denied = await managerClient.SendAsync(CreateWriteOffRequest(route, $"write-off-denied-{suffix}")))
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "posting access without the existing Approve capability must not authorize a transit write-off");

        using var approverClient = _factory.CreateAuthenticatedClient(accountant, "Accountant");
        var idempotencyKey = $"write-off-api-{suffix}";
        using var response = await approverClient.SendAsync(CreateWriteOffRequest(route, idempotencyKey));
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "write-off response was: {0}",
            await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<TransferTransitSettlementView>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        envelope.Data!.SettlementType.Should().Be(TransferTransitSettlementType.WrittenOff);
        envelope.Data.StockTransactionId.Should().BeNull();
        envelope.Data.TotalValue.Should().Be(50m);
        envelope.Data.Reason.Should().Be("Lost during carrier handoff");
        envelope.Data.SourceDocumentLineId.Should().Be(line.DocumentLineId);

        using var replayResponse = await approverClient.SendAsync(CreateWriteOffRequest(route, idempotencyKey));
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await replayResponse.Content.ReadFromJsonAsync<
            ApiResponse<TransferTransitSettlementView>>();
        replay!.Data!.Id.Should().Be(envelope.Data.Id);
        replay.Data.StockTransactionId.Should().BeNull();

        using var reportResponse = await approverClient.GetAsync(
            $"/api/v1/transfer-orders/aging?companyId={company.CompanyId}&pageSize=10");
        reportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reportEnvelope = await reportResponse.Content
            .ReadFromJsonAsync<ApiResponse<TransferAgingReconciliationPage>>();
        var report = reportEnvelope!.Data!.Lines.Should().ContainSingle().Subject;
        report.DispatchedQuantity.Should().Be(10);
        report.WrittenOffQuantity.Should().Be(4);
        report.OutstandingTransitQuantity.Should().Be(6);
        report.DispatchedValue.Should().Be(125m);
        report.WrittenOffValue.Should().Be(50m);
        report.OutstandingTransitValue.Should().Be(75m);
        report.QuantityConservationVariance.Should().Be(0);
        report.ValueConservationVariance.Should().Be(0m);
        report.SettlementLedgerQuantityVariance.Should().Be(0);
        report.SettlementLedgerValueVariance.Should().Be(0m);
        report.SettlementValuationPostingCountVariance.Should().Be(0);
        report.SettlementValuationQuantityVariance.Should().Be(0);

        await using var verify = fixture.CreateContext("test-tenant");
        (await verify.TransferTransitSettlements.ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<TransferTransitSettlement>(settlement =>
                settlement.StockTransactionId == null &&
                settlement.SettlementType == TransferTransitSettlementType.WrittenOff &&
                settlement.Quantity == 4 && settlement.TotalValue == 50m &&
                settlement.SourceDocumentLineId == new DocumentLineIdentityId(line.DocumentLineId));
        (await verify.StockTransactions.CountAsync(transaction => transaction.ItemId == company.ItemIds[0]))
            .Should().Be(transactionCountBefore);
        (await verify.StockValuationEntries.CountAsync(entry => entry.ItemId == company.ItemIds[0]))
            .Should().Be(valuationEntryCountBefore);
        (await verify.StockInHand.AsNoTracking()
            .Where(stock => stock.ItemId == company.ItemIds[0])
            .OrderBy(stock => stock.LocationId)
            .Select(stock => new { stock.LocationId, stock.Quantity, stock.ReservedQuantity, stock.QuarantinedQuantity })
            .ToListAsync()).Should().BeEquivalentTo(stockBefore);
        (await verify.StockValuationBuckets.AsNoTracking()
            .Where(bucket => bucket.ItemId == company.ItemIds[0])
            .OrderBy(bucket => bucket.LocationId)
            .Select(bucket => new { bucket.LocationId, bucket.Quantity, bucket.Value })
            .ToListAsync()).Should().BeEquivalentTo(valuationBucketsBefore);
    }

    [PostgreSqlFact]
    public async Task Fragmented_transit_and_settlement_history_is_aggregated_without_dropping_totals()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var company = await SeedCompanyAsync(_factory, fixture, "test-tenant", $"F{suffix}", 1);
        var user = await _factory.EnsurePersonaUserAsync("Manager", suffix);
        await GrantViewAsync(company.CompanyId, user.Id);
        using var client = _factory.CreateAuthenticatedClient(user, "Manager");
        var order = await CreateOrderAsync(
            _factory,
            company,
            [new TransferOrderLineRequest(company.ItemIds[0], 64)],
            $"aging-fragmented-{suffix}",
            approve: true);
        var line = order.Lines.Single();
        var history = Enumerable.Range(0, 64)
            .Select(index => new SyntheticTransitEvent(
                DispatchQuantity: 1,
                DispatchValue: 12.5m,
                SettlementQuantity: index < 32 ? 1 : 0,
                SettlementValue: index < 32 ? 12.5m : 0m,
                DispatchedAt: DateTimeOffset.UtcNow.AddDays(-index - 1)))
            .ToArray();
        await SeedSyntheticHistoryAsync(company, order, line, suffix, history);

        using var response = await client.GetAsync(
            $"/api/v1/transfer-orders/aging?companyId={company.CompanyId}&pageSize=1");
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "report response was: {0}",
            await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<TransferAgingReconciliationPage>>();
        var report = envelope!.Data!.Lines.Should().ContainSingle().Subject;
        report.DispatchedQuantity.Should().Be(64);
        report.ReceivedQuantity.Should().Be(32);
        report.OutstandingTransitQuantity.Should().Be(32);
        report.DispatchedValue.Should().Be(800m);
        report.ReceivedValue.Should().Be(400m);
        report.OutstandingTransitValue.Should().Be(400m);
        report.QuantityConservationVariance.Should().Be(0);
        report.ValueConservationVariance.Should().Be(0m);
        report.DispatchLedgerQuantityVariance.Should().Be(0);
        report.DispatchLedgerValueVariance.Should().Be(0m);
        report.DispatchValuationPostingCountVariance.Should().Be(0);
        report.DispatchValuationQuantityVariance.Should().Be(0);
        report.SettlementLedgerQuantityVariance.Should().Be(0);
        report.SettlementLedgerValueVariance.Should().Be(0m);
        report.SettlementValuationPostingCountVariance.Should().Be(0);
        report.SettlementValuationQuantityVariance.Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Zero_value_missing_and_misquantified_valuation_postings_are_visible()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var company = await SeedCompanyAsync(_factory, fixture, "test-tenant", $"Z{suffix}", 1);
        var user = await _factory.EnsurePersonaUserAsync("Manager", suffix);
        await GrantViewAsync(company.CompanyId, user.Id);
        using var client = _factory.CreateAuthenticatedClient(user, "Manager");
        var order = await CreateOrderAsync(
            _factory,
            company,
            [new TransferOrderLineRequest(company.ItemIds[0], 3)],
            $"aging-zero-valuation-{suffix}",
            approve: true);
        var line = order.Lines.Single();
        await SeedSyntheticHistoryAsync(
            company,
            order,
            line,
            suffix,
            [
                new SyntheticTransitEvent(
                    DispatchQuantity: 1,
                    DispatchValue: 0m,
                    IncludeDispatchValuation: false,
                    SettlementQuantity: 1,
                    SettlementValue: 0m,
                    IncludeSettlementValuation: false),
                new SyntheticTransitEvent(
                    DispatchQuantity: 2,
                    DispatchValue: 0m,
                    DispatchValuationQuantity: 1)
            ]);

        using var response = await client.GetAsync(
            $"/api/v1/transfer-orders/aging?companyId={company.CompanyId}&pageSize=1");
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "report response was: {0}",
            await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<TransferAgingReconciliationPage>>();
        var report = envelope!.Data!.Lines.Should().ContainSingle().Subject;
        report.DispatchedValue.Should().Be(0m);
        report.ReceivedValue.Should().Be(0m);
        report.DispatchLedgerValueVariance.Should().Be(0m);
        report.SettlementLedgerValueVariance.Should().Be(0m);
        report.DispatchLedgerQuantityVariance.Should().Be(0);
        report.SettlementLedgerQuantityVariance.Should().Be(0);
        report.DispatchValuationPostingCountVariance.Should().Be(1);
        report.DispatchValuationQuantityVariance.Should().Be(2);
        report.SettlementValuationPostingCountVariance.Should().Be(1);
        report.SettlementValuationQuantityVariance.Should().Be(1);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _otherTenantFactory.Dispose();
    }

    private async Task GrantViewAsync(int companyId, string userId)
    {
        await using var context = fixture.CreateContext("test-tenant");
        context.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyId,
            UserId = userId,
            Capabilities = CompanyCapability.View,
            IsActive = true
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedSyntheticHistoryAsync(
        CompanyScenario company,
        TransferOrderView order,
        TransferOrderLineView line,
        string suffix,
        IReadOnlyList<SyntheticTransitEvent> history)
    {
        await using var context = fixture.CreateContext("test-tenant");
        var itemId = company.ItemIds.Single();
        var dispatchDetails = history.Select((record, index) =>
        {
            var dispatchedAt = record.DispatchedAt ?? DateTimeOffset.UtcNow.AddDays(-index - 1);
            var transaction = new StockTransaction
            {
                ItemId = itemId,
                FromLocationId = company.SourceLocationId,
                ToLocationId = company.DestinationLocationId,
                Quantity = record.DispatchQuantity,
                TransactionType = TransactionType.TransferDispatch,
                TransactionDate = dispatchedAt.UtcDateTime,
                SourceLineReference = $"transfer-aging:{suffix}:{index}:dispatch",
                TenantId = "test-tenant"
            };
            context.StockTransactions.Add(transaction);
            if (record.IncludeDispatchValuation)
            {
                var valuationQuantity = record.DispatchValuationQuantity ?? record.DispatchQuantity;
                context.StockValuationEntries.Add(new StockValuationEntry
                {
                    StockTransaction = transaction,
                    ItemId = itemId,
                    LocationId = company.SourceLocationId,
                    EntryType = StockValuationEntryType.TransferOut,
                    Quantity = valuationQuantity,
                    UnitCost = valuationQuantity == 0 ? 0m : record.DispatchValue / valuationQuantity,
                    TotalValue = record.DispatchValue,
                    TenantId = "test-tenant"
                });
            }
            return new SyntheticDispatch(record, transaction, dispatchedAt);
        }).ToArray();

        await context.SaveChangesAsync();

        var transitEntries = dispatchDetails.Select(detail => new TransferTransitEntry
        {
            TransferOrderId = order.Id,
            TransferOrderLineId = line.Id,
            SourceDocumentLineId = new DocumentLineIdentityId(line.DocumentLineId),
            CompanyId = company.CompanyId,
            ItemId = itemId,
            FromLocationId = company.SourceLocationId,
            ToLocationId = company.DestinationLocationId,
            StockTransactionId = detail.Transaction.Id,
            Quantity = detail.Record.DispatchQuantity,
            UnitCost = detail.Record.DispatchQuantity == 0
                ? 0m
                : detail.Record.DispatchValue / detail.Record.DispatchQuantity,
            TotalValue = detail.Record.DispatchValue,
            IdempotencyKey = $"transfer-aging:{suffix}:{detail.Transaction.SourceLineReference}:entry",
            RequestHash = new string('a', 64),
            DispatchedBy = "synthetic-dispatcher",
            DispatchedAt = detail.DispatchedAt,
            TenantId = "test-tenant"
        }).ToArray();
        context.TransferTransitEntries.AddRange(transitEntries);
        await context.SaveChangesAsync();

        var settlementDetails = dispatchDetails
            .Select((detail, index) => new { Detail = detail, Transit = transitEntries[index] })
            .Where(pair => pair.Detail.Record.SettlementQuantity > 0)
            .Select(pair =>
            {
                var settledAt = pair.Detail.DispatchedAt.AddMinutes(1);
                var transaction = new StockTransaction
                {
                    ItemId = itemId,
                    FromLocationId = company.SourceLocationId,
                    ToLocationId = company.DestinationLocationId,
                    Quantity = pair.Detail.Record.SettlementQuantity,
                    TransactionType = TransactionType.TransferReceipt,
                    TransactionDate = settledAt.UtcDateTime,
                    SourceLineReference = $"transfer-aging:{suffix}:{pair.Detail.Transaction.SourceLineReference}:receipt",
                    TenantId = "test-tenant"
                };
                context.StockTransactions.Add(transaction);
                if (pair.Detail.Record.IncludeSettlementValuation)
                {
                    var valuationQuantity = pair.Detail.Record.SettlementValuationQuantity ??
                                            pair.Detail.Record.SettlementQuantity;
                    context.StockValuationEntries.Add(new StockValuationEntry
                    {
                        StockTransaction = transaction,
                        ItemId = itemId,
                        LocationId = company.DestinationLocationId,
                        EntryType = StockValuationEntryType.TransferIn,
                        Quantity = valuationQuantity,
                        UnitCost = valuationQuantity == 0
                            ? 0m
                            : pair.Detail.Record.SettlementValue / valuationQuantity,
                        TotalValue = pair.Detail.Record.SettlementValue,
                        TenantId = "test-tenant"
                    });
                }
                return new SyntheticSettlement(pair.Detail.Record, pair.Transit, transaction, settledAt);
            }).ToArray();

        await context.SaveChangesAsync();
        context.TransferTransitSettlements.AddRange(settlementDetails.Select(detail => new TransferTransitSettlement
        {
            TransferTransitEntryId = detail.Transit.Id,
            TransferOrderId = order.Id,
            TransferOrderLineId = line.Id,
            SourceDocumentLineId = new DocumentLineIdentityId(line.DocumentLineId),
            CompanyId = company.CompanyId,
            ItemId = itemId,
            FromLocationId = company.SourceLocationId,
            ToLocationId = company.DestinationLocationId,
            StockTransactionId = detail.Transaction.Id,
            Quantity = detail.Record.SettlementQuantity,
            SettlementType = TransferTransitSettlementType.Received,
            UnitCost = detail.Record.SettlementQuantity == 0
                ? 0m
                : detail.Record.SettlementValue / detail.Record.SettlementQuantity,
            TotalValue = detail.Record.SettlementValue,
            IdempotencyKey = $"transfer-aging:{suffix}:{detail.Transaction.SourceLineReference}:settlement",
            RequestHash = new string('b', 64),
            SettledBy = "synthetic-receiver",
            SettledAt = detail.SettledAt,
            TenantId = "test-tenant"
        }));
        var persistedLine = await context.TransferOrderLines.SingleAsync(candidate => candidate.Id == line.Id);
        persistedLine.DispatchedQuantity = history.Sum(record => record.DispatchQuantity);
        await context.SaveChangesAsync();
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

    private sealed record SyntheticTransitEvent(
        int DispatchQuantity,
        decimal DispatchValue,
        bool IncludeDispatchValuation = true,
        int? DispatchValuationQuantity = null,
        int SettlementQuantity = 0,
        decimal SettlementValue = 0m,
        bool IncludeSettlementValuation = true,
        int? SettlementValuationQuantity = null,
        DateTimeOffset? DispatchedAt = null);

    private sealed record SyntheticDispatch(
        SyntheticTransitEvent Record,
        StockTransaction Transaction,
        DateTimeOffset DispatchedAt);

    private sealed record SyntheticSettlement(
        SyntheticTransitEvent Record,
        TransferTransitEntry Transit,
        StockTransaction Transaction,
        DateTimeOffset SettledAt);
}
