using FluentAssertions;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;

namespace InventoryManagementSystem.Tests.Infrastructure;

public class UnitOfWorkContractTests
{
    [Fact]
    public void Repository_contract_does_not_expose_a_competing_commit_path()
    {
        typeof(IRepository<>).GetMethods()
            .Should().NotContain(method => method.Name == nameof(IUnitOfWork.SaveChangesAsync));
    }

    [Fact]
    public void UnitOfWork_exposes_the_transaction_boundary_required_by_services()
    {
        var contractMethods = typeof(IUnitOfWork).GetMethods()
            .Select(method => method.Name)
            .ToArray();

        contractMethods.Should().Contain(
            nameof(IUnitOfWork.BeginTransactionAsync),
            nameof(IUnitOfWork.CommitTransactionAsync),
            nameof(IUnitOfWork.RollbackTransactionAsync));

        foreach (var methodName in new[]
        {
            nameof(IUnitOfWork.BeginTransactionAsync),
            nameof(IUnitOfWork.CommitTransactionAsync),
            nameof(IUnitOfWork.RollbackTransactionAsync)
        })
        {
            typeof(UnitOfWork).GetMethod(methodName).Should().NotBeNull();
        }
    }
}
