using FluentAssertions;
using FluentValidation.TestHelper;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Validators;

namespace InventoryManagementSystem.Tests.Core.Validators;

public class ItemValidatorTests
{
    private readonly ItemValidator _sut = new();

    [Fact]
    public void Validate_ItemCodeEmpty_HasError()
    {
        var item = new Item { ItemCode = "", Description = "Test", Rate = 10m };
        var result = _sut.TestValidate(item);
        result.ShouldHaveValidationErrorFor(x => x.ItemCode);
    }

    [Fact]
    public void Validate_ItemCodeExceeds50_HasError()
    {
        var item = new Item { ItemCode = new string('A', 51), Description = "Test", Rate = 10m };
        var result = _sut.TestValidate(item);
        result.ShouldHaveValidationErrorFor(x => x.ItemCode);
    }

    [Fact]
    public void Validate_DescriptionExceeds500_HasError()
    {
        var item = new Item { ItemCode = "CODE", Description = new string('A', 501), Rate = 10m };
        var result = _sut.TestValidate(item);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Validate_RateZero_HasError()
    {
        var item = new Item { ItemCode = "CODE", Description = "Test", Rate = 0 };
        var result = _sut.TestValidate(item);
        result.ShouldHaveValidationErrorFor(x => x.Rate);
    }

    [Fact]
    public void Validate_RateNegative_HasError()
    {
        var item = new Item { ItemCode = "CODE", Description = "Test", Rate = -5m };
        var result = _sut.TestValidate(item);
        result.ShouldHaveValidationErrorFor(x => x.Rate);
    }

    [Fact]
    public void Validate_ValidItem_NoErrors()
    {
        var item = new Item { ItemCode = "CODE-001", Description = "Valid item", Rate = 10.50m };
        var result = _sut.TestValidate(item);
        result.ShouldNotHaveAnyValidationErrors();
    }
}

public class SupplierValidatorTests
{
    private readonly SupplierValidator _sut = new();

    [Fact]
    public void Validate_NameEmpty_HasError()
    {
        var supplier = new Supplier { Name = "" };
        var result = _sut.TestValidate(supplier);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameExceeds200_HasError()
    {
        var supplier = new Supplier { Name = new string('A', 201) };
        var result = _sut.TestValidate(supplier);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_ContactPersonExceeds200_HasError()
    {
        var supplier = new Supplier { Name = "Valid", ContactPerson = new string('A', 201) };
        var result = _sut.TestValidate(supplier);
        result.ShouldHaveValidationErrorFor(x => x.ContactPerson);
    }

    [Fact]
    public void Validate_PhoneExceeds50_HasError()
    {
        var supplier = new Supplier { Name = "Valid", Phone = new string('A', 51) };
        var result = _sut.TestValidate(supplier);
        result.ShouldHaveValidationErrorFor(x => x.Phone);
    }

    [Fact]
    public void Validate_EmailInvalid_HasError()
    {
        var supplier = new Supplier { Name = "Valid", Email = "not-an-email" };
        var result = _sut.TestValidate(supplier);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_EmailEmpty_NoError()
    {
        var supplier = new Supplier { Name = "Valid", Email = "" };
        var result = _sut.TestValidate(supplier);
        // Empty email is allowed (When condition)
        result.ShouldNotHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_ValidSupplier_NoErrors()
    {
        var supplier = new Supplier { Name = "ACME Corp", Email = "info@acme.com", Phone = "555-1234" };
        var result = _sut.TestValidate(supplier);
        result.ShouldNotHaveAnyValidationErrors();
    }
}

public class LocationValidatorTests
{
    private readonly LocationValidator _sut = new();

    [Fact]
    public void Validate_NameEmpty_HasError()
    {
        var location = new Location { Name = "" };
        var result = _sut.TestValidate(location);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameExceeds200_HasError()
    {
        var location = new Location { Name = new string('A', 201) };
        var result = _sut.TestValidate(location);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_AddressExceeds500_HasError()
    {
        var location = new Location { Name = "Valid", Address = new string('A', 501) };
        var result = _sut.TestValidate(location);
        result.ShouldHaveValidationErrorFor(x => x.Address);
    }

    [Fact]
    public void Validate_ValidLocation_NoErrors()
    {
        var location = new Location { Name = "Warehouse A", Address = "123 Main St" };
        var result = _sut.TestValidate(location);
        result.ShouldNotHaveAnyValidationErrors();
    }
}

public class PurchaseOrderValidatorTests
{
    private readonly PurchaseOrderValidator _sut = new();

    [Fact]
    public void Validate_PONumberEmpty_HasError()
    {
        var po = new PurchaseOrder { PONumber = "", SupplierId = 1, TotalAmount = 0 };
        var result = _sut.TestValidate(po);
        result.ShouldHaveValidationErrorFor(x => x.PONumber);
    }

    [Fact]
    public void Validate_PONumberExceeds50_HasError()
    {
        var po = new PurchaseOrder { PONumber = new string('A', 51), SupplierId = 1, TotalAmount = 0 };
        var result = _sut.TestValidate(po);
        result.ShouldHaveValidationErrorFor(x => x.PONumber);
    }

    [Fact]
    public void Validate_SupplierIdZero_HasError()
    {
        var po = new PurchaseOrder { PONumber = "PO-001", SupplierId = 0, TotalAmount = 0 };
        var result = _sut.TestValidate(po);
        result.ShouldHaveValidationErrorFor(x => x.SupplierId);
    }

    [Fact]
    public void Validate_TotalAmountNegative_HasError()
    {
        var po = new PurchaseOrder { PONumber = "PO-001", SupplierId = 1, TotalAmount = -10m };
        var result = _sut.TestValidate(po);
        result.ShouldHaveValidationErrorFor(x => x.TotalAmount);
    }

    [Fact]
    public void Validate_ValidPO_NoErrors()
    {
        var po = new PurchaseOrder { PONumber = "PO-001", SupplierId = 1, TotalAmount = 100m };
        var result = _sut.TestValidate(po);
        result.ShouldNotHaveAnyValidationErrors();
    }
}

public class StockTransactionValidatorTests
{
    private readonly StockTransactionValidator _sut = new();

    [Fact]
    public void Validate_ItemIdZero_HasError()
    {
        var tx = new StockTransaction { ItemId = 0, FromLocationId = 1, Quantity = 10, TransactionType = TransactionType.Receive };
        var result = _sut.TestValidate(tx);
        result.ShouldHaveValidationErrorFor(x => x.ItemId);
    }

    [Fact]
    public void Validate_FromLocationIdZero_HasError()
    {
        var tx = new StockTransaction { ItemId = 1, FromLocationId = 0, Quantity = 10, TransactionType = TransactionType.Receive };
        var result = _sut.TestValidate(tx);
        result.ShouldHaveValidationErrorFor(x => x.FromLocationId);
    }

    [Fact]
    public void Validate_QuantityZero_HasError()
    {
        var tx = new StockTransaction { ItemId = 1, FromLocationId = 1, Quantity = 0, TransactionType = TransactionType.Receive };
        var result = _sut.TestValidate(tx);
        result.ShouldHaveValidationErrorFor(x => x.Quantity);
    }

    [Fact]
    public void Validate_NotesExceeds500_HasError()
    {
        var tx = new StockTransaction { ItemId = 1, FromLocationId = 1, Quantity = 10, TransactionType = TransactionType.Receive, Notes = new string('A', 501) };
        var result = _sut.TestValidate(tx);
        result.ShouldHaveValidationErrorFor(x => x.Notes);
    }

    [Fact]
    public void Validate_TransferWithoutToLocationId_HasError()
    {
        var tx = new StockTransaction { ItemId = 1, FromLocationId = 1, Quantity = 10, TransactionType = TransactionType.Transfer, ToLocationId = 0 };
        var result = _sut.TestValidate(tx);
        result.ShouldHaveValidationErrorFor(x => x.ToLocationId);
    }

    [Fact]
    public void Validate_ReceiveWithoutToLocationId_NoError()
    {
        var tx = new StockTransaction { ItemId = 1, FromLocationId = 1, Quantity = 10, TransactionType = TransactionType.Receive, ToLocationId = null };
        var result = _sut.TestValidate(tx);
        result.ShouldNotHaveValidationErrorFor(x => x.ToLocationId);
    }

    [Fact]
    public void Validate_TransferWithToLocationId_NoError()
    {
        var tx = new StockTransaction { ItemId = 1, FromLocationId = 1, ToLocationId = 2, Quantity = 10, TransactionType = TransactionType.Transfer };
        var result = _sut.TestValidate(tx);
        result.ShouldNotHaveValidationErrorFor(x => x.ToLocationId);
    }

    [Fact]
    public void Validate_ValidTransaction_NoErrors()
    {
        var tx = new StockTransaction { ItemId = 1, FromLocationId = 1, Quantity = 10, TransactionType = TransactionType.Receive };
        var result = _sut.TestValidate(tx);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
