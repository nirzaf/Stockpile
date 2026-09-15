namespace InventoryManagementSystem.Core.Models;

/// <summary>A single calendar-day observation used to train the demand forecast.</summary>
public sealed record DailyDemandObservation(DateTime Date, float Quantity);
