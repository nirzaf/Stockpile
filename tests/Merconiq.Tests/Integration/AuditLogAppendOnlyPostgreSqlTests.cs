using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class AuditLogAppendOnlyPostgreSqlTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Audit_logs_reject_update_delete_and_truncate_at_the_database_boundary()
    {
        fixture.EnsureEnabled();
        var tenantId = $"audit-append-only-{Guid.NewGuid():N}";
        await using (var seed = fixture.CreateContext(tenantId))
        {
            seed.AuditLogs.Add(new AuditLog
            {
                EntityName = "PurchaseOrder",
                Action = "Update",
                Username = "stable-actor-id",
                Timestamp = DateTime.UtcNow,
                KeyValues = "{\"Id\":1}",
                OldValues = "{\"Status\":1}",
                NewValues = "{\"Status\":3}",
                ChangedColumns = "[\"Status\"]"
            });
            await seed.SaveChangesAsync();
        }

        await using (var update = fixture.CreateContext(tenantId))
        {
            var act = () => update.AuditLogs.ExecuteUpdateAsync(
                rows => rows.SetProperty(row => row.Username, "forged-actor"));
            await AssertDatabaseRejection(act);
        }

        await using (var delete = fixture.CreateContext(tenantId))
        {
            var act = () => delete.AuditLogs.ExecuteDeleteAsync();
            await AssertDatabaseRejection(act);
        }

        await using (var truncate = fixture.CreateContext(tenantId))
        {
            var act = () => truncate.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"AuditLogs\"");
            await AssertDatabaseRejection(act);
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.AuditLogs.CountAsync()).Should().Be(1);
    }

    private static async Task AssertDatabaseRejection(Func<Task> operation)
    {
        var failure = await operation.Should().ThrowAsync<Exception>();
        failure.Which.GetBaseException().Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("55000");
    }
}
