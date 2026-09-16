namespace Merconiq.Core.Entities;

/// <summary>Immutable, typed identity for a business document; never a display number.</summary>
public readonly record struct DocumentIdentityId
{
    public DocumentIdentityId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A document identity cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static DocumentIdentityId New() => new(Guid.NewGuid());
}
