using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Merconiq.Tests.Infrastructure;

public class AuditTrailTests
{
    [Fact]
    public async Task SaveChanges_records_insert_and_update_values()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options, new TestTenantContext("default"));
        var item = new Item
        {
            ItemCode = "AUDIT-001",
            Description = "Before update",
            Rate = 10m
        };

        context.Items.Add(item);
        await context.SaveChangesAsync();

        var insertAudit = await context.AuditLogs
            .SingleAsync(audit => audit.EntityName == nameof(Item) && audit.Action == "Insert");
        insertAudit.Username.Should().Be("System");
        JsonDocument.Parse(insertAudit.NewValues!).RootElement
            .GetProperty("Description")
            .GetString()
            .Should()
            .Be("Before update");

        item.Description = "After update";
        await context.SaveChangesAsync();

        var updateAudit = await context.AuditLogs
            .SingleAsync(audit => audit.EntityName == nameof(Item) && audit.Action == "Update");
        updateAudit.ChangedColumns.Should().Contain("Description");
        JsonDocument.Parse(updateAudit.OldValues!).RootElement
            .GetProperty("Description")
            .GetString()
            .Should()
            .Be("Before update");
        JsonDocument.Parse(updateAudit.NewValues!).RootElement
            .GetProperty("Description")
            .GetString()
            .Should()
            .Be("After update");
    }

    [Fact]
    public async Task SaveChangesAs_records_the_server_supplied_audit_identity()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options, new TestTenantContext("default"));
        context.Items.Add(new Item { ItemCode = "AUDIT-ACTOR", Description = "Actor test", Rate = 1m });

        await context.SaveChangesAsAsync("Approver Name [stable-user-id]");

        var audit = await context.AuditLogs.SingleAsync(row => row.EntityName == nameof(Item));
        audit.Username.Should().Be("Approver Name [stable-user-id]");
    }

    [Fact]
    public async Task SaveChanges_rejects_mutation_of_existing_audit_rows()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options, new TestTenantContext("default"));
        context.Items.Add(new Item { ItemCode = "AUDIT-IMMUTABLE", Description = "Immutable test", Rate = 1m });
        await context.SaveChangesAsync();
        var audit = await context.AuditLogs.SingleAsync(row => row.EntityName == nameof(Item));

        audit.Username = "forged-user";
        var act = () => context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Audit logs are append-only and cannot be updated or deleted.");
    }
}
