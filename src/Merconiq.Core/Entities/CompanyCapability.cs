namespace Merconiq.Core.Entities;

/// <summary>Explicit permissions that can be granted to a user within one company.</summary>
[Flags]
public enum CompanyCapability
{
    None = 0,
    View = 1 << 0,
    Edit = 1 << 1,
    Approve = 1 << 2,
    Post = 1 << 3,
    Reverse = 1 << 4,
    Administer = 1 << 5
}
