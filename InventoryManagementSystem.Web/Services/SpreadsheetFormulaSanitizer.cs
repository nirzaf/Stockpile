using System.Buffers;

namespace InventoryManagementSystem.Web.Services;

/// <summary>Prevents spreadsheet applications from evaluating untrusted text as formulas.</summary>
public static class SpreadsheetFormulaSanitizer
{
    private static readonly SearchValues<char> FormulaPrefixes = SearchValues.Create("=+-@");

    public static string SanitizeText(string? value)
    {
        if (string.IsNullOrEmpty(value) || !FormulaPrefixes.Contains(value[0]))
        {
            return value ?? string.Empty;
        }

        return $"'{value}";
    }
}
