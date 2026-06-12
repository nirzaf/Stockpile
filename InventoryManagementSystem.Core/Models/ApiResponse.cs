using System.Collections.Generic;

namespace InventoryManagementSystem.Core.Models;

/// <summary>Standard envelope for successful and failed API responses with a payload.</summary>
/// <typeparam name="T">The payload type.</typeparam>
public class ApiResponse<T>
{
    /// <summary>True when the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>The response payload, or <see langword="null"/> on failure.</summary>
    public T? Data { get; set; }

    /// <summary>Top-level error message on failure.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional list of detailed validation or business errors.</summary>
    public List<string>? Errors { get; set; }

    /// <summary>Creates a success response wrapping <paramref name="data"/>.</summary>
    /// <param name="data">The payload to return.</param>
    /// <returns>A successful <see cref="ApiResponse{T}"/>.</returns>
    public static ApiResponse<T> CreateSuccess(T data) => new() { Success = true, Data = data };

    /// <summary>Creates a failure response.</summary>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="errors">Optional list of detailed errors.</param>
    /// <returns>A failed <see cref="ApiResponse{T}"/>.</returns>
    public static ApiResponse<T> CreateFailure(string message, List<string>? errors = null) => new() { Success = false, ErrorMessage = message, Errors = errors };
}

/// <summary>Standard envelope for API responses without a payload (e.g. delete or no-op).</summary>
public class ApiResponse
{
    /// <summary>True when the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Top-level error message on failure.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional list of detailed validation or business errors.</summary>
    public List<string>? Errors { get; set; }

    /// <summary>Creates a success response with no payload.</summary>
    /// <returns>A successful <see cref="ApiResponse"/>.</returns>
    public static ApiResponse CreateSuccess() => new() { Success = true };

    /// <summary>Creates a failure response.</summary>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="errors">Optional list of detailed errors.</param>
    /// <returns>A failed <see cref="ApiResponse"/>.</returns>
    public static ApiResponse CreateFailure(string message, List<string>? errors = null) => new() { Success = false, ErrorMessage = message, Errors = errors };
}
