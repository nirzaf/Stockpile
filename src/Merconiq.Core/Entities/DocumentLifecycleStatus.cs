namespace Merconiq.Core.Entities;

/// <summary>Retained lifecycle states shared by numbered business documents.</summary>
public enum DocumentLifecycleStatus
{
    Draft,
    Active,
    Cancelled,
    Voided
}
