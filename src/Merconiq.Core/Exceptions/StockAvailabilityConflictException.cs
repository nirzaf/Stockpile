namespace Merconiq.Core.Exceptions;

/// <summary>Raised when a stock operation would use quantity held by reservations.</summary>
public sealed class StockAvailabilityConflictException(string message) : InvalidOperationException(message);
