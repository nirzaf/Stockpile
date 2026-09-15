using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace InventoryManagementSystem.Tests.Infrastructure;

public class AuditTrailTests
{
    [Fact]
    public async Task SaveChanges_records_insert_and_update_values()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options);
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
}
