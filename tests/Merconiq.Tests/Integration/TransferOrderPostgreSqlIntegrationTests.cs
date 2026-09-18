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
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TransferOrderPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
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
    public async Task Amendment_replaces_lines_preserves_retained_identity_and_releases_approved_reservations()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-amend-lines-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 50, unitCost: 10m);

        await using var operation = fixture.CreateContext(tenantId);
        var orders = CreateService(operation, tenantId);
        var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
        var original = (await orders.GetByIdAsync(seeded.OrderId))!;
        var retainedLine = original.Lines.Single();

        await orders.AmendAsync(seeded.OrderId, new CreateTransferOrderRequest(
            seeded.CompanyId,
            seeded.SourceLocationId,
            seeded.DestinationLocationId,
            [
                new TransferOrderLineRequest(seeded.ItemId, 50, LineId: retainedLine.Id),
                new TransferOrderLineRequest(seeded.ItemId, 10)
            ]), scope);

        var draftWithAddedLine = (await orders.GetByIdAsync(seeded.OrderId))!;
        draftWithAddedLine.Status.Should().Be(TransferOrderStatus.Draft);
        var lineToRemove = draftWithAddedLine.Lines.Single(line => line.Id != retainedLine.Id);
        lineToRemove.DocumentLineId.Should().NotBe(seeded.DocumentLineId);

        await orders.ApproveAsync(seeded.OrderId, scope);
        var approved = (await orders.GetByIdAsync(seeded.OrderId))!;
        approved.Status.Should().Be(TransferOrderStatus.Approved);

        await orders.AmendAsync(seeded.OrderId, new CreateTransferOrderRequest(
            seeded.CompanyId,
            seeded.SourceLocationId,
            seeded.DestinationLocationId,
            [
                new TransferOrderLineRequest(seeded.ItemId, 45, LineId: retainedLine.Id),
                new TransferOrderLineRequest(seeded.ItemId, 5)
            ]), scope);

        var amended = (await orders.GetByIdAsync(seeded.OrderId))!;
        amended.Status.Should().Be(TransferOrderStatus.Draft);
        amended.DocumentId.Should().Be(original.DocumentId);
        amended.Number.Should().Be(original.Number);
        amended.Lines.Should().HaveCount(2);
        var retainedAfterAmendment = amended.Lines.Single(line => line.Id == retainedLine.Id);
        retainedAfterAmendment.DocumentLineId.Should().Be(seeded.DocumentLineId);
        retainedAfterAmendment.Quantity.Should().Be(45);
        amended.Lines.Should().NotContain(line => line.Id == lineToRemove.Id);
        var newlyAddedLine = amended.Lines.Single(line => line.Id != retainedLine.Id);
        newlyAddedLine.DocumentLineId.Should().NotBe(seeded.DocumentLineId);
        newlyAddedLine.DocumentLineId.Should().NotBe(lineToRemove.DocumentLineId);
        newlyAddedLine.Quantity.Should().Be(5);

        var documentId = new DocumentIdentityId(original.DocumentId);
        var lineIdentities = await operation.DocumentLineIdentities
            .Where(identity => identity.DocumentId == documentId)
            .ToListAsync();
        lineIdentities.Should().HaveCount(2);
        lineIdentities.Should().ContainSingle(identity => identity.Id.Value == seeded.DocumentLineId);
        lineIdentities.Should().ContainSingle(identity => identity.Id.Value == newlyAddedLine.DocumentLineId);
        lineIdentities.Should().NotContain(identity => identity.Id.Value == lineToRemove.DocumentLineId);

        (await operation.StockReservations.ToListAsync()).Should().NotBeEmpty()
            .And.OnlyContain(reservation => reservation.Status == StockReservationStatus.Released);
        (await operation.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.SourceLocationId))
            .ReservedQuantity.Should().Be(0);
        (await operation.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.TransferDispatch)).Should().Be(0);
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
    public async Task Failed_transit_settlement_outbox_enqueue_rolls_back_stock_valuation_and_settlement()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-settlement-rollback-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 30, unitCost: 10m);
        int transitEntryId;

        await using (var dispatchContext = fixture.CreateContext(tenantId))
        {
            var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
            var dispatch = await CreateService(dispatchContext, tenantId).DispatchAsync(
                seeded.OrderId, seeded.LineId, 30, "settlement-rollback-dispatch", "dispatcher", scope);
            transitEntryId = dispatch.Id;
        }

        await using (var operation = fixture.CreateContext(tenantId))
        {
            var orders = CreateService(operation, tenantId, new ThrowingWebhookDispatcher());
            await FluentAssertions.FluentActions.Invoking(() => orders.ResolveTransitAsync(
                    seeded.OrderId,
                    seeded.LineId,
                    transitEntryId,
                    new TransferTransitSettlementRequest(20, TransferTransitSettlementType.Received),
                    "settlement-rollback-receive",
                    "receiver",
                    new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true))))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Webhook enqueue failed.");
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand.CountAsync(stock => stock.ItemId == seeded.ItemId &&
                stock.LocationId == seeded.DestinationLocationId)).Should().Be(0);
        (await verify.StockValuationBuckets.CountAsync(bucket => bucket.ItemId == seeded.ItemId &&
                bucket.LocationId == seeded.DestinationLocationId)).Should().Be(0);
        (await verify.TransferTransitSettlements.CountAsync()).Should().Be(0);
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.TransferReceipt)).Should().Be(0);
        (await verify.StockValuationEntries.CountAsync(entry =>
            entry.EntryType == StockValuationEntryType.TransferIn)).Should().Be(0);
        (await verify.TransferTransitEntries.SingleAsync(entry => entry.Id == transitEntryId))
            .Should().Match<TransferTransitEntry>(entry => entry.Quantity == 30 && entry.TotalValue == 300m);
        (await verify.TransferOrders.SingleAsync(order => order.Id == seeded.OrderId))
            .Status.Should().Be(TransferOrderStatus.InTransit);
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
    public async Task Transit_settlement_supports_partial_receipt_return_and_idempotent_replay()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-settlement-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 30, unitCost: 10m);

        await using var operation = fixture.CreateContext(tenantId);
        var orders = CreateService(operation, tenantId);
        var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
        var dispatch = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 30, "settle-dispatch", "dispatcher", scope);

        var received = await orders.ResolveTransitAsync(
            seeded.OrderId,
            seeded.LineId,
            dispatch.Id,
            new TransferTransitSettlementRequest(20, TransferTransitSettlementType.Received),
            "settle-receive",
            "receiver",
            scope);
        received.SettlementType.Should().Be(TransferTransitSettlementType.Received);
        received.Quantity.Should().Be(20);
        received.TotalValue.Should().Be(200m);

        var replay = await orders.ResolveTransitAsync(
            seeded.OrderId,
            seeded.LineId,
            dispatch.Id,
            new TransferTransitSettlementRequest(20, TransferTransitSettlementType.Received),
            "settle-receive",
            "another-receiver",
            scope);
        replay.Should().Be(received);

        await FluentAssertions.FluentActions.Invoking(() => orders.ResolveTransitAsync(
                seeded.OrderId,
                seeded.LineId,
                dispatch.Id,
                new TransferTransitSettlementRequest(21, TransferTransitSettlementType.Received),
                "settle-receive",
                "receiver",
                scope))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The transit idempotency key was already used with a different request.");

        await FluentAssertions.FluentActions.Invoking(() => orders.ResolveTransitAsync(
                seeded.OrderId,
                seeded.LineId,
                dispatch.Id,
                new TransferTransitSettlementRequest(11, TransferTransitSettlementType.Received),
                "settle-over-remaining",
                "receiver",
                scope))
            .Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Settlement quantity exceeds the remaining transit quantity of 10.");

        var returned = await orders.ResolveTransitAsync(
            seeded.OrderId,
            seeded.LineId,
            dispatch.Id,
            new TransferTransitSettlementRequest(10, TransferTransitSettlementType.Returned),
            "settle-return",
            "receiver",
            scope);
        returned.SettlementType.Should().Be(TransferTransitSettlementType.Returned);
        returned.TotalValue.Should().Be(100m);

        var order = await orders.GetByIdAsync(seeded.OrderId);
        order!.Status.Should().Be(TransferOrderStatus.Completed);
        order.Lines.Single().ReceivedQuantity.Should().Be(20);
        (await operation.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.SourceLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 80 && stock.ReservedQuantity == 0);
        (await operation.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 20 && stock.QuarantinedQuantity == 0);
        (await operation.TransferTransitSettlements.ToListAsync()).Should().HaveCount(2);
        (await operation.StockValuationEntries.Where(entry =>
                entry.EntryType == StockValuationEntryType.TransferIn ||
                entry.EntryType == StockValuationEntryType.TransferReturn)
            .SumAsync(entry => entry.TotalValue)).Should().Be(300m);
        (await operation.StockValuationBuckets.Where(bucket => bucket.ItemId == seeded.ItemId)
                .SumAsync(bucket => bucket.Value)).Should().Be(1000m);

        await FluentAssertions.FluentActions.Invoking(() => operation.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"TransferTransitSettlements\" SET \"Reason\" = {"forbidden"}"))
            .Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(exception => exception.SqlState == "55000");
    }

    [PostgreSqlFact]
    public async Task Transit_settlement_conserves_the_captured_value_when_unit_cost_rounding_leaves_a_remainder()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-settlement-value-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 3, unitCost: 0.33333333m);

        await using var operation = fixture.CreateContext(tenantId);
        var orders = CreateService(operation, tenantId);
        var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
        var dispatch = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 3, "settle-value-dispatch", "dispatcher", scope);
        dispatch.UnitCost.Should().Be(0.333333m);
        dispatch.TotalValue.Should().Be(1m);

        var first = await orders.ResolveTransitAsync(
            seeded.OrderId, seeded.LineId, dispatch.Id,
            new TransferTransitSettlementRequest(1, TransferTransitSettlementType.Received),
            "settle-value-first", "receiver", scope);
        var second = await orders.ResolveTransitAsync(
            seeded.OrderId, seeded.LineId, dispatch.Id,
            new TransferTransitSettlementRequest(1, TransferTransitSettlementType.Received),
            "settle-value-second", "receiver", scope);
        var final = await orders.ResolveTransitAsync(
            seeded.OrderId, seeded.LineId, dispatch.Id,
            new TransferTransitSettlementRequest(1, TransferTransitSettlementType.Received),
            "settle-value-final", "receiver", scope);

        new[] { first.TotalValue, second.TotalValue, final.TotalValue }
            .Should().Equal(0.333333m, 0.333333m, 0.333334m);
        (await operation.StockValuationEntries
            .Where(entry => entry.EntryType == StockValuationEntryType.TransferIn)
            .SumAsync(entry => entry.TotalValue)).Should().Be(dispatch.TotalValue);
        (await operation.TransferTransitSettlements.SumAsync(settlement => settlement.TotalValue))
            .Should().Be(dispatch.TotalValue);
    }

    [PostgreSqlFact]
    public async Task Transfer_order_completion_aggregates_settlements_across_all_dispatch_entries()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transit-multi-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 30, unitCost: 10m);

        await using var operation = fixture.CreateContext(tenantId);
        var orders = CreateService(operation, tenantId);
        var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
        var firstDispatch = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 10, "multi-dispatch-first", "dispatcher", scope);
        await orders.ResolveTransitAsync(
            seeded.OrderId, seeded.LineId, firstDispatch.Id,
            new TransferTransitSettlementRequest(10, TransferTransitSettlementType.Returned),
            "multi-settle-first", "receiver", scope);

        (await orders.GetByIdAsync(seeded.OrderId))!.Status.Should().Be(TransferOrderStatus.InTransit,
            "a return without destination receipt is not a partial receipt");

        var secondDispatch = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 20, "multi-dispatch-second", "dispatcher", scope);
        await orders.ResolveTransitAsync(
            seeded.OrderId, seeded.LineId, secondDispatch.Id,
            new TransferTransitSettlementRequest(20, TransferTransitSettlementType.Received),
            "multi-settle-second", "receiver", scope);

        var completed = await orders.GetByIdAsync(seeded.OrderId);
        completed!.Status.Should().Be(TransferOrderStatus.Completed);
        completed.Lines.Single().ReceivedQuantity.Should().Be(20);
        (await operation.TransferTransitSettlements.SumAsync(settlement => settlement.Quantity)).Should().Be(30);
        (await operation.StockInHand.SingleAsync(stock => stock.ItemId == seeded.ItemId &&
                stock.LocationId == seeded.SourceLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 80 && stock.ReservedQuantity == 0);
        (await operation.StockInHand.SingleAsync(stock => stock.ItemId == seeded.ItemId &&
                stock.LocationId == seeded.DestinationLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 20 && stock.QuarantinedQuantity == 0);
    }

    [PostgreSqlFact]
    public async Task Concurrent_duplicate_receipts_commit_one_settlement_and_one_stock_movement()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-settlement-race-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 20, unitCost: 10m);
        int transitEntryId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var dispatch = await CreateService(setup, tenantId).DispatchAsync(
                seeded.OrderId,
                seeded.LineId,
                20,
                "race-dispatch",
                "dispatcher",
                new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true)));
            transitEntryId = dispatch.Id;
        }

        async Task<TransferTransitSettlementView> ResolveAsync(string applicationName)
        {
            await using var context = fixture.CreateContext(tenantId, applicationName);
            return await CreateService(context, tenantId).ResolveTransitAsync(
                seeded.OrderId,
                seeded.LineId,
                transitEntryId,
                new TransferTransitSettlementRequest(20, TransferTransitSettlementType.Received),
                "race-receive",
                "receiver",
                new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true)));
        }

        await using var lockContext = fixture.CreateContext(tenantId, "transfer-settlement-lock-holder");
        var lockUnitOfWork = new UnitOfWork(lockContext);
        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockHolder = lockUnitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await lockUnitOfWork.AcquireTenantOperationLockAsync("organization-state");
            lockAcquired.SetResult();
            await releaseLock.Task;
        });

        TransferTransitSettlementView[] results;
        var bothRequestsWaitedForLock = false;
        try
        {
            await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var first = ResolveAsync("transfer-settlement-race-a");
            var second = ResolveAsync("transfer-settlement-race-b");
            bothRequestsWaitedForLock = await WaitForAdvisoryLockWaitersAsync(
                fixture.ConnectionString,
                ["transfer-settlement-race-a", "transfer-settlement-race-b"]);
            releaseLock.TrySetResult();
            await lockHolder;
            results = await Task.WhenAll(first, second);
        }
        finally
        {
            releaseLock.TrySetResult();
            await lockHolder;
        }

        bothRequestsWaitedForLock.Should().BeTrue(
            "both settlement requests must be observed waiting on the PostgreSQL advisory lock before it is released");
        results[0].Should().Be(results[1]);
        await using var verify = fixture.CreateContext(tenantId);
        (await verify.TransferTransitSettlements.ToListAsync()).Should().ContainSingle();
        (await verify.StockTransactions.CountAsync(transaction =>
                transaction.TransactionType == TransactionType.TransferReceipt))
            .Should().Be(1);
        (await verify.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 20 && stock.QuarantinedQuantity == 0);
    }

    [PostgreSqlFact]
    public async Task Transit_quarantine_requires_reason_and_preserves_unavailable_valued_stock()
    {
        fixture.EnsureEnabled();
        var tenantId = $"transfer-quarantine-{Guid.NewGuid():N}";
        var seeded = await CreateApprovedTransferAsync(tenantId, 10, unitCost: 10m);

        await using var operation = fixture.CreateContext(tenantId);
        var orders = CreateService(operation, tenantId);
        var scope = new StockMutationScope(seeded.CompanyId, () => Task.FromResult(true));
        var dispatch = await orders.DispatchAsync(
            seeded.OrderId, seeded.LineId, 10, "quarantine-dispatch", "dispatcher", scope);

        await FluentAssertions.FluentActions.Invoking(() => orders.ResolveTransitAsync(
                seeded.OrderId,
                seeded.LineId,
                dispatch.Id,
                new TransferTransitSettlementRequest(10, TransferTransitSettlementType.Quarantined),
                "quarantine-receive",
                "receiver",
                scope))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("A reason is required for quarantined transit stock.*");

        var quarantined = await orders.ResolveTransitAsync(
            seeded.OrderId,
            seeded.LineId,
            dispatch.Id,
            new TransferTransitSettlementRequest(
                10,
                TransferTransitSettlementType.Quarantined,
                Reason: "Damaged on arrival"),
            "quarantine-receive",
            "receiver",
            scope);
        quarantined.Reason.Should().Be("Damaged on arrival");
        (await operation.StockInHand.SingleAsync(stock =>
                stock.ItemId == seeded.ItemId && stock.LocationId == seeded.DestinationLocationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 10 && stock.QuarantinedQuantity == 10);
        (await operation.TransferTransitSettlements.SingleAsync())
            .SettlementType.Should().Be(TransferTransitSettlementType.Quarantined);
    }

    private static async Task<bool> WaitForAdvisoryLockWaitersAsync(
        string connectionString,
        string[] applicationNames)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE application_name = ANY(@application_names)
                  AND wait_event_type = 'Lock'
                  AND wait_event = 'advisory'
                """,
                connection);
            command.Parameters.AddWithValue("application_names", applicationNames);
            var waiting = (long)(await command.ExecuteScalarAsync())!;
            if (waiting >= applicationNames.Length)
                return true;

            await Task.Delay(25);
        }

        return false;
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
            NullLogger<TransferOrderService>.Instance,
            new Repository<TransferTransitSettlement>(context));
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
