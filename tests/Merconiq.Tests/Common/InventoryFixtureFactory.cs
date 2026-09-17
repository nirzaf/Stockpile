using AutoFixture;
using Merconiq.Core.Entities;

namespace Merconiq.Tests.Common;

public static class InventoryFixtureFactory
{
    public static Fixture Create()
    {
        var fixture = new Fixture();
        fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList()
            .ForEach(b => fixture.Behaviors.Remove(b));
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        fixture.Customize<Item>(c => c
            .With(x => x.ItemCode, () => "WIDGET-001")
            .With(x => x.Description, () => "Default test widget")
            .With(x => x.Rate, 10.00m)
            .With(x => x.IsActive, true)
            .With(x => x.BaseUnitId, (int?)null)
            .With(x => x.PurchaseUnitId, (int?)null)
            .With(x => x.SalesUnitId, (int?)null)
            .Without(x => x.Supplier)
            .Without(x => x.StockInHands)
            .Without(x => x.StockTransactions)
            .Without(x => x.OrderDetails));

        fixture.Customize<Supplier>(c => c
            .With(x => x.Name, () => "Test Supplier")
            .Without(x => x.Items)
            .Without(x => x.PurchaseOrders));

        fixture.Customize<Location>(c => c
            .With(x => x.Name, () => "Test Location")
            .Without(x => x.StockInHands));

        fixture.Customize<StockInHand>(c => c
            .With(x => x.Quantity, 100)
            .With(x => x.QuarantinedQuantity, 0)
            .Without(x => x.Item)
            .Without(x => x.Location));

        fixture.Customize<StockTransaction>(c => c
            .With(x => x.Quantity, 10)
            .With(x => x.TransactionType, TransactionType.Receive)
            .Without(x => x.Item)
            .Without(x => x.FromLocation)
            .Without(x => x.ToLocation));

        fixture.Customize<PurchaseOrder>(c => c
            .With(x => x.PONumber, () => "PO-001")
            .With(x => x.Status, PurchaseOrderStatus.Pending)
            .With(x => x.TotalAmount, 0m)
            .Without(x => x.Supplier)
            .Without(x => x.DocumentIdentity)
            .Without(x => x.OrderDetails));

        fixture.Customize<OrderDetail>(c => c
            .With(x => x.Quantity, 1)
            .With(x => x.UnitPrice, 10.00m)
            .Without(x => x.PurchaseOrder)
            .Without(x => x.DocumentLineIdentity)
            .Without(x => x.Item));

        return fixture;
    }
}
