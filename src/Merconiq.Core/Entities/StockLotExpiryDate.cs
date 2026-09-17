namespace Merconiq.Core.Entities;

/// <summary>Canonicalizes lot expiry dates for the existing PostgreSQL timestamptz columns.</summary>
public static class StockLotExpiryDate
{
    /// <summary>
    /// Preserves the supplied calendar date and represents it as UTC midnight. Expiry is a
    /// date-only value, so the input time and <see cref="DateTime.Kind"/> do not shift its date.
    /// </summary>
    public static DateTime? Normalize(DateTime? expiryDate) => expiryDate is DateTime value
        ? new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc)
        : null;
}
