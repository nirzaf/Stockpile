using System;

namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Represents an immutable audit record. Maps to the <c>AuditLogs</c> table.
/// Populated automatically by <c>InventoryDbContext.SaveChangesAsync</c>.
/// </summary>
public class AuditLog
{
    public int Id { get; set; }

    /// <summary>CLR type name of the audited entity.</summary>
    public string EntityName { get; set; } = null!;

    /// <summary>Action performed (e.g. <c>Added</c>, <c>Modified</c>, <c>Deleted</c>).</summary>
    public string Action { get; set; } = null!;

    /// <summary>Identity name of the user that triggered the action.</summary>
    public string Username { get; set; } = null!;

    /// <summary>UTC timestamp of the action.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>JSON-serialised primary key values.</summary>
    public string? KeyValues { get; set; }

    /// <summary>JSON-serialised original property values (for modifications).</summary>
    public string? OldValues { get; set; }

    /// <summary>JSON-serialised new property values (for additions and modifications).</summary>
    public string? NewValues { get; set; }

    /// <summary>Comma-separated list of property names that changed (for modifications).</summary>
    public string? ChangedColumns { get; set; }
}
