namespace Merconiq.Core.Entities;

/// <summary>Immutable, typed identity for a line within a business document.</summary>
public readonly record struct DocumentLineIdentityId
{
    public DocumentLineIdentityId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A document-line identity cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static DocumentLineIdentityId New() => new(Guid.NewGuid());
}
