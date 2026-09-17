using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
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
    public async Task Transit_receipt_is_partial_idempotent_valued_and_rejects_over_receipt_without_mutation()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-receipt-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 30, unitCost: 10m);
        await using (var setup = fixture.CreateContext(tenantId))
        {
            setup.WebhookSubscriptions.Add(new WebhookSubscription
            {
                EventType = "TransferOrder.Received",
                Url = "https://example.invalid/transfer-received"
            });
            await setup.SaveChangesAsync();
        }

        await using var operation = fixture.CreateContext(tenantId);
        var dispatcher = new WebhookDispatcher(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<WebhookDispatcher>.Instance,
            operation);
        var orders = CreateService(operation, tenantId, dispatcher);
        var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
        var dispatch = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 30, "receipt-dispatch-30", "warehouse-user-42", scope);
        dispatch.Quantity.Should().Be(30);
        dispatch.TotalValue.Should().Be(300m);

        var receipt = await orders.ReceiveTransitAsync(
            seeded.OrderId, dispatch.Id, 20, "receive-first-20", "receiver-user-7", scope);

        receipt.Should().Match<TransferTransitReceiptView>(view =>
            view.TransferTransitEntryId == dispatch.Id && view.Quantity == 20 &&
            view.RemainingQuantity == 10 && view.UnitCost == 10m &&
            view.TotalValue == 200m && view.RemainingValue == 100m &&
            view.SourceDocumentLineId == seeded.DocumentLineId &&
            view.FromLocationId == seeded.SourceLocationId &&
            view.ToLocationId == seeded.DestinationLocationId && view.ReceivedBy == "receiver-user-7");
        receipt.StockTransactionId.Should().BeGreaterThan(0);

        var replay = await orders.ReceiveTransitAsync(
            seeded.OrderId, dispatch.Id, 20, "receive-first-20", "another-receiver", scope);
        replay.Should().Be(receipt);

        (await operation.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 20);
        (await operation.StockValuationBuckets.SingleAsync(bucket =>
                bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.DestinationLocationId))
            .Should().Match<StockValuationBucket>(bucket => bucket.Quantity == 20 && bucket.Value == 200m);
        (await operation.TransferTransitEntries.SingleAsync(entry => entry.Id == dispatch.Id))
            .Should().Match<TransferTransitEntry>(entry => entry.Quantity == 30 && entry.TotalValue == 300m);
        (await operation.TransferTransitReceipts.SingleAsync())
            .Should().Match<TransferTransitReceipt>(entry =>
                entry.Quantity == 20 && entry.RemainingQuantity == 10 && entry.TotalValue == 200m &&
                entry.RemainingValue == 100m && entry.StockTransactionId == receipt.StockTransactionId);
        (await operation.StockTransactions.SingleAsync(transaction =>
                transaction.Id == receipt.StockTransactionId))
            .Should().Match<StockTransaction>(transaction =>
                transaction.TransactionType == TransactionType.TransferReceipt && transaction.Quantity == 20 &&
                transaction.UnitCost == 10m && transaction.SourceLineReference!.StartsWith(
                    $"TransitReceipt:{dispatch.Id}:", StringComparison.Ordinal));
        (await operation.StockValuationEntries.SingleAsync(entry =>
                entry.StockTransactionId == receipt.StockTransactionId))
            .Should().Match<StockValuationEntry>(entry =>
                entry.EntryType == StockValuationEntryType.TransferIn && entry.Quantity == 20 &&
                entry.UnitCost == 10m && entry.TotalValue == 200m);
        (await operation.WebhookDeliveries.CountAsync(delivery =>
            delivery.EventType == "TransferOrder.Received")).Should().Be(1);

        var beforeOverReceipt = new
        {
            DestinationQuantity = await operation.StockInHand
                .Where(stock => stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId)
                .SumAsync(stock => stock.Quantity),
            DestinationValue = await operation.StockValuationBuckets
                .Where(bucket => bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.DestinationLocationId)
                .SumAsync(bucket => bucket.Value),
            Receipts = await operation.TransferTransitReceipts.CountAsync(),
            ReceiptTransactions = await operation.StockTransactions.CountAsync(transaction =>
                transaction.TransactionType == TransactionType.TransferReceipt),
            TransferInEntries = await operation.StockValuationEntries.CountAsync(entry =>
                entry.EntryType == StockValuationEntryType.TransferIn),
            ReceiptOutbox = await operation.WebhookDeliveries.CountAsync(delivery =>
                delivery.EventType == "TransferOrder.Received")
        };

        await FluentAssertions.FluentActions.Invoking(() => orders.ReceiveTransitAsync(
                seeded.OrderId, dispatch.Id, 11, "receive-over-10", "receiver-user-7", scope))
            .Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Receipt exceeds the outstanding transfer transit quantity.");

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand
                .Where(stock => stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId)
                .SumAsync(stock => stock.Quantity)).Should().Be(beforeOverReceipt.DestinationQuantity);
        (await verify.StockValuationBuckets
                .Where(bucket => bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.DestinationLocationId)
                .SumAsync(bucket => bucket.Value)).Should().Be(beforeOverReceipt.DestinationValue);
        (await verify.TransferTransitReceipts.CountAsync()).Should().Be(beforeOverReceipt.Receipts);
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.TransferReceipt)).Should().Be(beforeOverReceipt.ReceiptTransactions);
        (await verify.StockValuationEntries.CountAsync(entry =>
            entry.EntryType == StockValuationEntryType.TransferIn)).Should().Be(beforeOverReceipt.TransferInEntries);
        (await verify.WebhookDeliveries.CountAsync(delivery =>
            delivery.EventType == "TransferOrder.Received")).Should().Be(beforeOverReceipt.ReceiptOutbox);

        var sourceValue = await verify.StockValuationBuckets
            .Where(bucket => bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.SourceLocationId)
            .SumAsync(bucket => bucket.Value);
        (sourceValue + receipt.RemainingValue + beforeOverReceipt.DestinationValue).Should().Be(1000m);
    }

    [PostgreSqlFact]
    public async Task Concurrent_receipts_cannot_accept_the_same_remaining_transit_twice()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-receipt-race-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 30, unitCost: 10m);
        int transitEntryId;
        await using (var dispatchContext = fixture.CreateContext(tenantId))
        {
            var orders = CreateService(dispatchContext, tenantId);
            var dispatch = await orders.DispatchAsync(
                seeded.OrderId, seeded.LineId, 30, "race-dispatch-30", "warehouse-user-42",
                new StockMutationScope(seeded.CompanyId));
            transitEntryId = dispatch.Id;
        }

        async Task<(bool Succeeded, Exception? Error)> TryReceiveAsync(string key)
        {
            await using var context = fixture.CreateContext(tenantId);
            var orders = CreateService(context, tenantId);
            try
            {
                await orders.ReceiveTransitAsync(
                    seeded.OrderId, transitEntryId, 20, key, "receiver-user-7",
                    new StockMutationScope(seeded.CompanyId));
                return (true, null);
            }
            catch (Exception exception)
            {
                return (false, exception);
            }
        }

        var attempts = await Task.WhenAll(
            TryReceiveAsync("race-receipt-a"),
            TryReceiveAsync("race-receipt-b"));

        attempts.Count(attempt => attempt.Succeeded).Should().Be(1);
        attempts.Single(attempt => !attempt.Succeeded).Error
            .Should().BeOfType<StockAvailabilityConflictException>();
        await using var verify = fixture.CreateContext(tenantId);
        (await verify.TransferTransitReceipts.SumAsync(receipt => receipt.Quantity)).Should().Be(20);
        (await verify.TransferTransitReceipts.SumAsync(receipt => receipt.TotalValue)).Should().Be(200m);
        (await verify.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId))
            .Quantity.Should().Be(20);
        (await verify.StockValuationBuckets.SingleAsync(bucket =>
                bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.DestinationLocationId))
            .Should().Match<StockValuationBucket>(bucket => bucket.Quantity == 20 && bucket.Value == 200m);
    }

    [PostgreSqlFact]
    public async Task Failed_receipt_outbox_enqueue_rolls_back_destination_stock_valuation_and_receipt()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-receipt-rollback-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 30, unitCost: 10m);
        int transitEntryId;
        await using (var dispatchContext = fixture.CreateContext(tenantId))
        {
            var dispatch = await CreateService(dispatchContext, tenantId).DispatchAsync(
                seeded.OrderId, seeded.LineId, 30, "rollback-dispatch-30", "warehouse-user-42",
                new StockMutationScope(seeded.CompanyId));
            transitEntryId = dispatch.Id;
        }

        await using (var operation = fixture.CreateContext(tenantId))
        {
            var orders = CreateService(operation, tenantId, new ThrowingWebhookDispatcher());
            await FluentAssertions.FluentActions.Invoking(() => orders.ReceiveTransitAsync(
                    seeded.OrderId, transitEntryId, 20, "rollback-receipt-20", "receiver-user-7",
                    new StockMutationScope(seeded.CompanyId)))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Webhook enqueue failed.");
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand.CountAsync(stock =>
            stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId)).Should().Be(0);
        (await verify.StockValuationBuckets.CountAsync(bucket =>
            bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.DestinationLocationId)).Should().Be(0);
        (await verify.TransferTransitReceipts.CountAsync()).Should().Be(0);
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.TransferReceipt)).Should().Be(0);
        (await verify.StockValuationEntries.CountAsync(entry =>
            entry.EntryType == StockValuationEntryType.TransferIn)).Should().Be(0);
        (await verify.TransferTransitEntries.SingleAsync(entry => entry.Id == transitEntryId))
            .Should().Match<TransferTransitEntry>(entry => entry.Quantity == 30 && entry.TotalValue == 300m);
    }

    [PostgreSqlFact]
    public async Task Dispatch_is_partial_idempotent_and_conserves_source_value_in_transit()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-dispatch-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 50, unitCost: 10m);

        await using var operation = fixture.CreateContext(tenantId);
        var orders = CreateService(operation, tenantId);
        var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
        var first = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 30, "dispatch-part-1", "warehouse-user-42", scope);

        first.Quantity.Should().Be(30);
        first.TotalValue.Should().Be(300m);
        first.UnitCost.Should().Be(10m);
        first.SourceDocumentLineId.Should().Be(seeded.DocumentLineId);
        first.FromLocationId.Should().Be(seeded.SourceLocationId);
        first.DispatchedBy.Should().Be("warehouse-user-42");

        var replay = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 30, "dispatch-part-1", "another-user", scope);
        replay.Should().Be(first);
        await FluentAssertions.FluentActions.Invoking(() => orders.DispatchAsync(
                seeded.OrderId, seeded.LineId, 29, "dispatch-part-1", "warehouse-user-42", scope))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The dispatch idempotency key was already used with a different request.");

        await FluentAssertions.FluentActions.Invoking(() => orders.DispatchAsync(
                seeded.OrderId, seeded.LineId, 21, "dispatch-over", "warehouse-user-42", scope))
            .Should().ThrowAsync<StockAvailabilityConflictException>();

        (await operation.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.SourceLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 70 && stock.ReservedQuantity == 20);
        (await operation.StockValuationBuckets.SingleAsync(bucket =>
                bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.SourceLocationId))
            .Should().Match<StockValuationBucket>(bucket => bucket.Quantity == 70 && bucket.Value == 700m);
        (await operation.TransferTransitEntries.ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<TransferTransitEntry>(entry =>
                entry.Quantity == 30 && entry.UnitCost == 10m && entry.TotalValue == 300m &&
                entry.CompanyId == seeded.CompanyId && entry.FromLocationId == seeded.SourceLocationId &&
                entry.SourceDocumentLineId.Value == seeded.DocumentLineId);

        var second = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 20, "dispatch-part-2", "warehouse-user-42", scope);
        second.Quantity.Should().Be(20);
        second.TotalValue.Should().Be(200m);
        (await operation.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.SourceLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 50 && stock.ReservedQuantity == 0);
        (await operation.StockValuationBuckets.SingleAsync(bucket =>
                bucket.ItemId == seeded.ItemId && bucket.LocationId == seeded.SourceLocationId))
            .Should().Match<StockValuationBucket>(bucket => bucket.Quantity == 50 && bucket.Value == 500m);
        (await operation.TransferTransitEntries.SumAsync(entry => entry.Quantity)).Should().Be(50);
        (await operation.TransferTransitEntries.SumAsync(entry => entry.TotalValue)).Should().Be(500m);
        (await operation.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.TransferDispatch)).Should().Be(2);
        (await operation.StockValuationEntries
                .Where(entry => entry.EntryType == StockValuationEntryType.TransferOut)
                .SumAsync(entry => entry.TotalValue)).Should().Be(500m);

        await FluentAssertions.FluentActions.Invoking(() => orders.CancelAsync(seeded.OrderId, scope))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A transfer order cannot be cancelled after any quantity is dispatched.");
        await FluentAssertions.FluentActions.Invoking(() => orders.AmendAsync(
                seeded.OrderId,
                new CreateTransferOrderRequest(
                    seeded.CompanyId,
                    seeded.SourceLocationId,
                    seeded.DestinationLocationId,
                    [new TransferOrderLineRequest(seeded.ItemId, 50, LineId: seeded.LineId)]),
                scope))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A transfer order cannot be amended after any quantity is dispatched.");
    }

    [PostgreSqlFact]
    public async Task Failed_dispatch_outbox_enqueue_rolls_back_stock_reservation_valuation_and_transit()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-dispatch-rollback-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 50, unitCost: 10m);

        await using (var operation = fixture.CreateContext(tenantId))
        {
            var throwingDispatcher = new ThrowingWebhookDispatcher();
            var orders = CreateService(operation, tenantId, throwingDispatcher);
            var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
            await FluentAssertions.FluentActions.Invoking(() => orders.DispatchAsync(
                    seeded.OrderId, seeded.LineId, 30, "dispatch-rollback", "warehouse-user-42", scope))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Webhook enqueue failed.");
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.SourceLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 100 && stock.ReservedQuantity == 50);
        (await verify.StockReservations.SingleAsync()).Should()
            .Match<StockReservation>(reservation => reservation.ConsumedQuantity == 0 &&
                reservation.Status == StockReservationStatus.Active);
        (await verify.StockValuationBuckets.SingleAsync()).Should()
            .Match<StockValuationBucket>(bucket => bucket.Quantity == 100 && bucket.Value == 1000m);
        (await verify.TransferTransitEntries.CountAsync()).Should().Be(0);
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.TransferDispatch)).Should().Be(0);
        (await verify.TransferOrderLines.SingleAsync()).DispatchedQuantity.Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Dispatch_refuses_unvalued_source_stock_without_inventing_a_cost()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-dispatch-unvalued-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 50, unitCost: null);

        await using (var operation = fixture.CreateContext(tenantId))
        {
            var orders = CreateService(operation, tenantId);
            var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
            await FluentAssertions.FluentActions.Invoking(() => orders.DispatchAsync(
                    seeded.OrderId, seeded.LineId, 30, "dispatch-unvalued", "warehouse-user-42", scope))
                .Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("Transfer dispatch requires an existing valued source bucket.");
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.SourceLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 100 && stock.ReservedQuantity == 50);
        (await verify.TransferTransitEntries.CountAsync()).Should().Be(0);
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.TransferDispatch)).Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Dispatch_refuses_lot_costing_until_M03_defines_a_lot_valuation_scope()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-dispatch-lot-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(
            tenantId, 20, unitCost: null, batchNumber: "LOT-281", expiryDate: DateTime.UtcNow.AddYears(1).Date);

        await using (var operation = fixture.CreateContext(tenantId))
        {
            var orders = CreateService(operation, tenantId);
            var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
            await FluentAssertions.FluentActions.Invoking(() => orders.DispatchAsync(
                    seeded.OrderId, seeded.LineId, 10, "dispatch-lot", "warehouse-user-42", scope))
                .Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("Valued transfer dispatch is limited to unbatched stock until lot valuation is implemented.");
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand.SingleAsync(stock => stock.ItemId == seeded.ItemId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 20 && stock.ReservedQuantity == 20);
        (await verify.TransferTransitEntries.CountAsync()).Should().Be(0);
    }

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
                [new TransferOrderLineRequest(itemId, 3), new TransferOrderLineRequest(itemId, 2)]), "transfer-create-1");
            (await orders.GetRecentForCompaniesAsync([companyId]))
                .Should().ContainSingle().Which.Id.Should().Be(order.Id);
            await orders.ApproveAsync(order.Id, new StockMutationScope(companyId));

            (await operation.StockTransactions.CountAsync()).Should().Be(0);
            (await operation.StockInHand.SingleAsync(stockRow =>
                    stockRow.ItemId == itemId && stockRow.LocationId == sourceLocationId))
                .Should().Match<StockInHand>(stockRow => stockRow.Quantity == 10 && stockRow.ReservedQuantity == 5);
            (await operation.StockReservations.CountAsync()).Should().Be(2);
            (await operation.StockReservations.SumAsync(reservation => reservation.Quantity)).Should().Be(5);
            (await operation.StockReservations.ToListAsync()).Should().OnlyContain(reservation =>
                reservation.Status == StockReservationStatus.Active &&
                reservation.ExpiresAt > DateTimeOffset.UtcNow.AddYears(50));

            await using var genericMutation = fixture.CreateContext(tenantId);
            var genericStock = CreateStockService(genericMutation, tenantId);
            var sourceLineReference = await genericMutation.StockReservations
                .OrderBy(reservation => reservation.Id)
                .Select(reservation => reservation.SourceLineReference)
                .FirstAsync();
            await FluentAssertions.FluentActions.Invoking(() => genericStock.ConsumeReservationAsync(
                    new ConsumeStockReservationRequest(sourceLineReference, 1),
                    new StockMutationScope(companyId)))
                .Should().ThrowAsync<UnauthorizedAccessException>();
            await FluentAssertions.FluentActions.Invoking(() => genericStock.CreateReservationAsync(
                    new CreateStockReservationRequest(itemId, sourceLocationId, 3, sourceLineReference),
                    new StockMutationScope(companyId)))
                .Should().ThrowAsync<UnauthorizedAccessException>();

            await using var cancellation = fixture.CreateContext(tenantId);
            await CreateService(cancellation, tenantId)
                .CancelAsync(order.Id, new StockMutationScope(companyId));
            (await cancellation.StockInHand.SingleAsync(stockRow =>
                    stockRow.ItemId == itemId && stockRow.LocationId == sourceLocationId))
                .ReservedQuantity.Should().Be(0);
            (await cancellation.StockReservations.ToListAsync()).Should().OnlyContain(reservation =>
                reservation.Status == StockReservationStatus.Released);
            (await cancellation.TransferOrders.SingleAsync()).Status.Should().Be(TransferOrderStatus.Cancelled);
        }
    }

    private async Task<TransferFixture> CreateApprovedTransferAsync(
        string tenantId,
        int transferQuantity,
        decimal? unitCost,
        string? batchNumber = null,
        DateTime? expiryDate = null)
    {
        int companyId;
        int sourceLocationId;
        int destinationLocationId;
        int itemId;
        int orderId;
        int lineId;
        Guid documentLineId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var company = new Company { Code = $"TD-{suffix}", LegalName = "Transfer dispatch company" };
            setup.Companies.Add(company);
            await setup.SaveChangesAsync();
            var sourceBranch = new Branch { CompanyId = company.Id, Code = $"S-{suffix}", Name = "Source" };
            var destinationBranch = new Branch { CompanyId = company.Id, Code = $"D-{suffix}", Name = "Destination" };
            setup.Branches.AddRange(sourceBranch, destinationBranch);
            await setup.SaveChangesAsync();
            var source = new Location { BranchId = sourceBranch.Id, Name = "Dispatch source" };
            var destination = new Location { BranchId = destinationBranch.Id, Name = "Dispatch destination" };
            var item = new Item { ItemCode = $"TD-ITEM-{suffix}", Description = "Valued transfer item" };
            setup.Locations.AddRange(source, destination);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();

            await CreateStockService(setup, tenantId).ReceiveStockAsync(
                item.Id,
                source.Id,
                batchNumber is null ? 100 : transferQuantity,
                "Synthetic transfer fixture",
                batchNumber,
                expiryDate,
                unitCost,
                new StockMutationScope(company.Id));

            var orders = CreateService(setup, tenantId);
            var order = await orders.CreateAsync(new CreateTransferOrderRequest(
                company.Id,
                source.Id,
                destination.Id,
                [new TransferOrderLineRequest(item.Id, transferQuantity, batchNumber, expiryDate)]),
                $"create-{Guid.NewGuid():N}");
            await orders.ApproveAsync(order.Id, new StockMutationScope(company.Id));

            companyId = company.Id;
            sourceLocationId = source.Id;
            destinationLocationId = destination.Id;
            itemId = item.Id;
            orderId = order.Id;
            lineId = order.Lines.Single().Id;
            documentLineId = order.Lines.Single().DocumentLineId;
        }

        return new TransferFixture(
            companyId, sourceLocationId, destinationLocationId, itemId, orderId, lineId, documentLineId);
    }

    private static TransferOrderService CreateService(
        InventoryDbContext context,
        string tenantId,
        IWebhookDispatcher? webhookDispatcher = null)
    {
        var tenant = new TestTenantContext(tenantId);
        var unitOfWork = new UnitOfWork(context);
        var dispatcher = webhookDispatcher ?? new Mock<IWebhookDispatcher>().Object;
        var stock = CreateStockService(context, tenant, unitOfWork, dispatcher);
        return new TransferOrderService(
            new Repository<TransferOrder>(context),
            new Repository<TransferOrderLine>(context),
            new Repository<TransferTransitEntry>(context),
            new Repository<TransferTransitReceipt>(context),
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
            dispatcher,
            NullLogger<TransferOrderService>.Instance);
    }

    private static StockService CreateStockService(InventoryDbContext context, string tenantId)
    {
        var tenant = new TestTenantContext(tenantId);
        return CreateStockService(context, tenant, new UnitOfWork(context));
    }

    private static StockService CreateStockService(
        InventoryDbContext context,
        TestTenantContext tenant,
        UnitOfWork unitOfWork,
        IWebhookDispatcher? webhookDispatcher = null) => new(
            new Repository<StockInHand>(context),
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            new Repository<Location>(context),
            new Repository<Branch>(context),
            unitOfWork,
            webhookDispatcher ?? new Mock<IWebhookDispatcher>().Object,
            tenant,
            NullLogger<StockService>.Instance,
            new Repository<StockValuationBucket>(context),
            new Repository<StockValuationEntry>(context),
            new Repository<StockReservation>(context),
            new Repository<StockReservationAllocation>(context));

    private sealed record TransferFixture(
        int CompanyId,
        int SourceLocationId,
        int DestinationLocationId,
        int ItemId,
        int OrderId,
        int LineId,
        Guid DocumentLineId);

    private sealed class ThrowingWebhookDispatcher : IWebhookDispatcher
    {
        public Task EnqueueAsync<T>(WebhookEvent<T> webhookEvent) =>
            throw new InvalidOperationException("Webhook enqueue failed.");

        public Task DispatchAsync<T>(WebhookEvent<T> webhookEvent) => Task.CompletedTask;
    }
}
