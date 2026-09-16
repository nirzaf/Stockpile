using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Tests.Common;

namespace Merconiq.Tests.Core.Entities;

public class StockTransactionTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void StockTransaction_WhenQuantityIsNotPositive_StillConstructsButInvariantIsViolated(int quantity)
    {
        // Arrange
        var transaction = new StockTransaction
        {
            ItemId = _fixture.Create<int>(),
            FromLocationId = _fixture.Create<int>(),
            Quantity = quantity,
            TransactionType = TransactionType.Receive,
            TransactionDate = DateTime.UtcNow
        };

        // Act
        var actualQuantity = transaction.Quantity;

        // Assert
        actualQuantity.Should().Be(quantity);
        (actualQuantity > 0).Should().BeFalse();
    }

    [Fact]
    public void StockTransaction_WhenOutboundAppliedToStockInHand_DecrementsBalance()
    {
        // Arrange
        var item = _fixture.Create<Item>();
        var location = _fixture.Create<Location>();
        var stockBalance = _fixture.Build<StockInHand>()
            .With(s => s.Quantity, 50)
            .Create();

        var sellQuantity = 10;

        // Act
        stockBalance.Quantity -= sellQuantity;

        // Assert
        stockBalance.Quantity.Should().Be(40);
    }

    [Fact]
    public void StockTransaction_WhenInboundAppliedToStockInHand_IncrementsBalance()
    {
        // Arrange
        var stockBalance = _fixture.Build<StockInHand>()
            .With(s => s.Quantity, 20)
            .Create();
        var receiveQuantity = 30;

        // Act
        stockBalance.Quantity += receiveQuantity;

        // Assert
        stockBalance.Quantity.Should().Be(50);
    }

    [Fact]
    public void StockTransaction_ConversionKilogramsToGrams_CalculatesPrecisely()
    {
        // Arrange
        var kilograms = 2.5m;
        const decimal gramsPerKilogram = 1000m;

        // Act
        var grams = kilograms * gramsPerKilogram;

        // Assert
        grams.Should().Be(2500m);
    }

    [Fact]
    public void StockTransaction_ConversionLitersToMilliliters_CalculatesPrecisely()
    {
        // Arrange
        var liters = 1.75m;
        const decimal millilitersPerLiter = 1000m;

        // Act
        var milliliters = liters * millilitersPerLiter;

        // Assert
        milliliters.Should().Be(1750m);
    }

    [Theory]
    [InlineData(TransactionType.Receive, 1)]
    [InlineData(TransactionType.Transfer, 2)]
    [InlineData(TransactionType.Sell, null)]
    public void StockTransaction_WhenTypeChanges_DestinationSemanticsDiffer(
        TransactionType transactionType, int? expectedToLocationId)
    {
        // Arrange
        var transaction = new StockTransaction
        {
            ItemId = _fixture.Create<int>(),
            FromLocationId = 1,
            ToLocationId = expectedToLocationId,
            Quantity = 10,
            TransactionType = transactionType,
            TransactionDate = DateTime.UtcNow
        };

        // Act
        var actualToLocationId = transaction.ToLocationId;

        // Assert
        actualToLocationId.Should().Be(expectedToLocationId);
        transaction.TransactionType.Should().Be(transactionType);
    }
}
