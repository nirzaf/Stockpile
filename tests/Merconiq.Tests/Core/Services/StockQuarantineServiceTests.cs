using System.Linq.Expressions;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Services;

public sealed class StockQuarantineServiceTests
{
    [Fact]
    public async Task Quarantine_uses_only_available_units_and_is_source_line_idempotent()
    {
        var harness = CreateHarness(quantity: 12, reserved: 3, quarantined: 2);
        var expiryDate = new DateTime(2030, 6, 30);
        var request = new ChangeStockQuarantineRequest(
            7, 11, 7, "quarantine-line-1", "LOT-7", expiryDate, "Quality inspection");
        var postChecks = 0;
        var scope = new StockMutationScope(null, () =>
        {
            postChecks++;
            return Task.FromResult(true);
        });

        var excessive = request with { Quantity = 8 };
        var rejectExcess = () => harness.Service.QuarantineStockAsync(excessive, scope);
        await rejectExcess.Should().ThrowAsync<StockAvailabilityConflictException>();
        harness.Stock.QuarantinedQuantity.Should().Be(2);
        harness.Stock.Quantity.Should().Be(12);
        harness.Transactions.Should().BeEmpty();
        harness.Webhooks.Should().BeEmpty();

        await harness.Service.QuarantineStockAsync(request, scope);
        await harness.Service.QuarantineStockAsync(request, scope);

        harness.Stock.Quantity.Should().Be(12);
        harness.Stock.ReservedQuantity.Should().Be(3);
        harness.Stock.QuarantinedQuantity.Should().Be(9);
        harness.Bucket.Quantity.Should().Be(12);
        harness.Bucket.Value.Should().Be(1200m);
        harness.Transactions.Should().ContainSingle().Which.Should().Match<StockTransaction>(transaction =>
            transaction.TransactionType == TransactionType.Quarantine &&
            transaction.SourceLineReference == request.SourceLineReference &&
            transaction.BatchNumber == "LOT-7" && transaction.ExpiryDate == expiryDate &&
            transaction.QuarantineReason == "Quality inspection" && transaction.Quantity == 7);
        harness.Webhooks.Should().ContainSingle().Which.Should().Match<WebhookEvent<StockQuarantineWebhookPayload>>(webhook =>
            webhook.EventType == "Stock.Quarantined" &&
            webhook.Payload.BatchNumber == "LOT-7" && webhook.Payload.ExpiryDate == expiryDate &&
            webhook.Payload.QuarantineReason == "Quality inspection");

        var mismatchedReplay = () => harness.Service.QuarantineStockAsync(
            request with { Reason = "Different reason" }, scope);
        await mismatchedReplay.Should().ThrowAsync<StockAvailabilityConflictException>();

        var missingExpiryReplay = () => harness.Service.QuarantineStockAsync(
            request with { ExpiryDate = null }, scope);
        await missingExpiryReplay.Should().ThrowAsync<StockAvailabilityConflictException>();

        var differentExpiryReplay = () => harness.Service.QuarantineStockAsync(
            request with { ExpiryDate = expiryDate.AddDays(1) }, scope);
        await differentExpiryReplay.Should().ThrowAsync<StockAvailabilityConflictException>();

        harness.Stock.QuarantinedQuantity.Should().Be(9);
        harness.Transactions.Should().ContainSingle();
        postChecks.Should().BeGreaterThan(0);

        var sellQuarantinedQuantity = () => harness.Service.SellStockAsync(
            request.ItemId,
            request.LocationId,
            2,
            "must be released first",
            request.BatchNumber,
            request.ExpiryDate,
            mutationScope: scope);
        await sellQuarantinedQuantity.Should().ThrowAsync<StockAvailabilityConflictException>();

        var reserveQuarantinedQuantity = () => harness.Service.CreateReservationAsync(
            new CreateStockReservationRequest(
                request.ItemId, request.LocationId, 2, "reservation-line-1", request.BatchNumber, request.ExpiryDate),
            scope);
        await reserveQuarantinedQuantity.Should().ThrowAsync<StockAvailabilityConflictException>();
        harness.Stock.Quantity.Should().Be(12);
        harness.Stock.QuarantinedQuantity.Should().Be(9);
    }

    [Fact]
    public async Task Release_requires_explicit_override_and_is_audited_and_idempotent()
    {
        var harness = CreateHarness(quantity: 12, reserved: 1, quarantined: 4);
        var request = new ChangeStockQuarantineRequest(
            7, 11, 2, "release-line-1", "LOT-7", new DateTime(2030, 6, 30), "Inspection passed");
        var postScope = new StockMutationScope(null, () => Task.FromResult(true));

        var missingOverride = () => harness.Service.ReleaseQuarantinedStockAsync(request, postScope);
        await missingOverride.Should().ThrowAsync<UnauthorizedAccessException>();
        harness.Stock.Quantity.Should().Be(12);
        harness.Stock.QuarantinedQuantity.Should().Be(4);
        harness.Transactions.Should().BeEmpty();
        harness.Webhooks.Should().BeEmpty();

        var deniedScope = postScope with
        {
            ReauthorizeQuarantinedStockOverride = () => Task.FromResult(false)
        };
        var deniedOverride = () => harness.Service.ReleaseQuarantinedStockAsync(request, deniedScope);
        await deniedOverride.Should().ThrowAsync<UnauthorizedAccessException>();
        harness.Stock.QuarantinedQuantity.Should().Be(4);

        var authorizedScope = postScope with
        {
            ReauthorizeQuarantinedStockOverride = () => Task.FromResult(true)
        };
        await harness.Service.ReleaseQuarantinedStockAsync(request, authorizedScope);
        await harness.Service.ReleaseQuarantinedStockAsync(request, authorizedScope);

        harness.Stock.Quantity.Should().Be(12);
        harness.Stock.ReservedQuantity.Should().Be(1);
        harness.Stock.QuarantinedQuantity.Should().Be(2);
        harness.Bucket.Quantity.Should().Be(12);
        harness.Bucket.Value.Should().Be(1200m);
        harness.Transactions.Should().ContainSingle().Which.Should().Match<StockTransaction>(transaction =>
            transaction.TransactionType == TransactionType.QuarantineRelease &&
            transaction.SourceLineReference == request.SourceLineReference &&
            transaction.QuarantineReason == "Inspection passed" &&
            transaction.BatchNumber == request.BatchNumber && transaction.ExpiryDate == request.ExpiryDate);
        harness.Webhooks.Should().ContainSingle().Which.Should().Match<WebhookEvent<StockQuarantineWebhookPayload>>(webhook =>
            webhook.EventType == "Stock.QuarantineReleased" &&
            webhook.Payload.QuarantineReason == "Inspection passed");

        var mismatchedReplay = () => harness.Service.ReleaseQuarantinedStockAsync(
            request with { Quantity = 3 }, authorizedScope);
        await mismatchedReplay.Should().ThrowAsync<StockAvailabilityConflictException>();
        harness.Stock.QuarantinedQuantity.Should().Be(2);
        harness.Transactions.Should().ContainSingle();
        harness.Webhooks.Should().ContainSingle();
    }

    [Fact]
    public async Task Quarantine_rejects_invalid_reason_and_source_reference_before_mutation()
    {
        var harness = CreateHarness(quantity: 5, reserved: 0, quarantined: 0);
        var valid = new ChangeStockQuarantineRequest(7, 11, 1, "line-1", "LOT-7", null, "Damaged packaging");

        var noReason = () => harness.Service.QuarantineStockAsync(valid with { Reason = "  " });
        await noReason.Should().ThrowAsync<ArgumentException>();

        var longReason = () => harness.Service.QuarantineStockAsync(valid with { Reason = new string('x', 501) });
        await longReason.Should().ThrowAsync<ArgumentException>();

        var noSourceLine = () => harness.Service.QuarantineStockAsync(valid with { SourceLineReference = " " });
        await noSourceLine.Should().ThrowAsync<ArgumentException>();

        harness.Stock.QuarantinedQuantity.Should().Be(0);
        harness.Transactions.Should().BeEmpty();
        harness.Webhooks.Should().BeEmpty();
    }

    [Fact]
    public async Task Quarantine_requires_expiry_date_when_batch_selects_a_dated_lot()
    {
        var harness = CreateHarness(quantity: 5, reserved: 0, quarantined: 0);
        var request = new ChangeStockQuarantineRequest(
            7, 11, 1, "quarantine-line-1", "LOT-7", null, "Quality review");

        var quarantine = () => harness.Service.QuarantineStockAsync(request);
        await quarantine.Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Provide the expiry date to identify this dated stock lot.");

        harness.Stock.Quantity.Should().Be(5);
        harness.Stock.QuarantinedQuantity.Should().Be(0);
        harness.Transactions.Should().BeEmpty();
        harness.Webhooks.Should().BeEmpty();
    }

    private static Harness CreateHarness(int quantity, int reserved, int quarantined)
    {
        var tenant = "quarantine-unit-test";
        var stock = new StockInHand
        {
            Id = 31,
            ItemId = 7,
            LocationId = 11,
            TenantId = tenant,
            Quantity = quantity,
            ReservedQuantity = reserved,
            QuarantinedQuantity = quarantined,
            BatchNumber = "LOT-7",
            ExpiryDate = new DateTime(2030, 6, 30)
        };
        var bucket = new StockValuationBucket
        {
            Id = 41,
            ItemId = 7,
            LocationId = 11,
            TenantId = tenant,
            Quantity = quantity,
            Value = 1200m
        };
        var location = new Location { Id = 11, Name = "Quarantine location", TenantId = tenant };
        var stockRows = new List<StockInHand> { stock };
        var transactions = new List<StockTransaction>();
        var webhooks = new List<WebhookEvent<StockQuarantineWebhookPayload>>();

        var stockRepo = new Mock<IRepository<StockInHand>>();
        stockRepo.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<StockInHand, bool>>>() ))
            .Returns((Expression<Func<StockInHand, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockInHand>>(stockRows.Where(predicate.Compile()).ToArray()));
        stockRepo.Setup(repository => repository.UpdateAsync(It.IsAny<StockInHand>()))
            .Returns(Task.CompletedTask);

        var transactionRepo = new Mock<IRepository<StockTransaction>>();
        transactionRepo.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>() ))
            .Returns((Expression<Func<StockTransaction, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockTransaction>>(transactions.Where(predicate.Compile()).ToArray()));
        transactionRepo.Setup(repository => repository.AddAsync(It.IsAny<StockTransaction>()))
            .Callback<StockTransaction>(transactions.Add)
            .ReturnsAsync((StockTransaction transaction) => transaction);

        var locationRepo = new Mock<IRepository<Location>>();
        locationRepo.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Location, bool>>>() ))
            .Returns((Expression<Func<Location, bool>> predicate) =>
                Task.FromResult<IEnumerable<Location>>(new[] { location }.Where(predicate.Compile()).ToArray()));

        var branchRepo = new Mock<IRepository<Branch>>();
        branchRepo.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Branch, bool>>>() ))
            .ReturnsAsync(Array.Empty<Branch>());

        var valuationRepo = new Mock<IRepository<StockValuationBucket>>();
        valuationRepo.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<StockValuationBucket, bool>>>() ))
            .Returns((Expression<Func<StockValuationBucket, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockValuationBucket>>(new[] { bucket }.Where(predicate.Compile()).ToArray()));

        var reservationRepo = new Mock<IRepository<StockReservation>>();
        reservationRepo.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<StockReservation, bool>>>() ))
            .ReturnsAsync(Array.Empty<StockReservation>());

        var itemRepo = new Mock<IRepository<Item>>();
        itemRepo.Setup(repository => repository.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Item?)null);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Task> operation, CancellationToken _, Func<Task<bool>>? _) => operation());
        unitOfWork.Setup(work => work.AcquireTenantOperationLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        unitOfWork.Setup(work => work.AcquireLocationLocksAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var webhookDispatcher = new Mock<IWebhookDispatcher>();
        webhookDispatcher.Setup(dispatcher => dispatcher.EnqueueAsync(
                It.IsAny<WebhookEvent<StockQuarantineWebhookPayload>>()))
            .Callback<WebhookEvent<StockQuarantineWebhookPayload>>(webhooks.Add)
            .Returns(Task.CompletedTask);

        var service = new StockService(
            stockRepo.Object,
            transactionRepo.Object,
            itemRepo.Object,
            locationRepo.Object,
            branchRepo.Object,
            unitOfWork.Object,
            webhookDispatcher.Object,
            new TestTenantContext(tenant),
            NullLogger<StockService>.Instance,
            valuationRepo.Object,
            new Mock<IRepository<StockValuationEntry>>().Object,
            reservationRepo.Object);

        return new Harness(service, stock, bucket, transactions, webhooks);
    }

    private sealed record Harness(
        StockService Service,
        StockInHand Stock,
        StockValuationBucket Bucket,
        List<StockTransaction> Transactions,
        List<WebhookEvent<StockQuarantineWebhookPayload>> Webhooks);
}
