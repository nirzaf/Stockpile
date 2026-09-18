namespace Merconiq.Core.Entities;

/// <summary>
/// A company-owned chart account definition. This is configuration only; it is not a journal
/// account mapping and does not participate in financial posting.
/// </summary>
public sealed class ChartOfAccount : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>The company that owns this account definition.</summary>
    public int CompanyId { get; set; }

    /// <summary>A company-supplied account code; no code sequence or defaults are provided.</summary>
    public string AccountCode { get; set; } = string.Empty;

    /// <summary>The company-supplied display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A company-supplied classification label. Its vocabulary has no posting semantics in this slice.
    /// </summary>
    public string AccountType { get; set; } = string.Empty;

    /// <summary>Optional parent account; when set, the parent must be active and a group account.</summary>
    public int? ParentAccountId { get; set; }

    /// <summary>Whether this account groups child accounts rather than representing a leaf.</summary>
    public bool IsGroupAccount { get; set; }

    /// <summary>Whether the definition is currently active for configuration queries.</summary>
    public bool IsActive { get; set; } = true;

    public ChartOfAccount? ParentAccount { get; set; }
    public ICollection<ChartOfAccount> ChildAccounts { get; set; } = new List<ChartOfAccount>();
}
