using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace Merconiq.Web.Services;

/// <summary>Builds downloadable tabular exports for the list pages.</summary>
public static class ExportFileBuilder
{
    /// <summary>Creates an RFC 4180-compatible UTF-8 CSV file.</summary>
    public static byte[] CreateCsv(IEnumerable<string> headers, IEnumerable<object?[]> rows)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, headers);

        foreach (var row in rows)
        {
            AppendCsvRow(builder, row);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(builder.ToString());
    }

    /// <summary>Creates an XLSX workbook with a formatted header row and autofit columns.</summary>
    public static byte[] CreateExcel(string worksheetName, IEnumerable<string> headers, IEnumerable<object?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(worksheetName);
        var headerValues = headers.ToArray();

        for (var column = 0; column < headerValues.Length; column++)
        {
            worksheet.Cell(1, column + 1).Value = SpreadsheetFormulaSanitizer.SanitizeText(headerValues[column]);
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Length; column++)
            {
                SetExcelValue(worksheet.Cell(rowNumber, column + 1), row[column]);
            }

            rowNumber++;
        }

        if (headerValues.Length > 0)
        {
            worksheet.Range(1, 1, 1, headerValues.Length).Style.Font.Bold = true;
            worksheet.SheetView.FreezeRows(1);
            worksheet.Columns(1, headerValues.Length).AdjustToContents();
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<object?> values)
    {
        builder.AppendJoin(',', values.Select(value => EscapeCsvValue(value)));
        builder.AppendLine();
    }

    private static string EscapeCsvValue(object? value)
    {
        var text = ConvertToSafeText(value);
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string ConvertToSafeText(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return value is string ? SpreadsheetFormulaSanitizer.SanitizeText(text) : text;
    }

    private static void SetExcelValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Value = string.Empty;
                break;
            case string text:
                cell.Value = SpreadsheetFormulaSanitizer.SanitizeText(text);
                break;
            case bool boolean:
                cell.Value = boolean;
                break;
            case byte number:
                cell.Value = number;
                break;
            case short number:
                cell.Value = number;
                break;
            case int number:
                cell.Value = number;
                break;
            case long number:
                cell.Value = number;
                break;
            case float number:
                cell.Value = number;
                break;
            case double number:
                cell.Value = number;
                break;
            case decimal number:
                cell.Value = number;
                break;
            case DateTime date:
                cell.Value = date;
                break;
            case DateTimeOffset date:
                cell.Value = date.DateTime;
                break;
            default:
                cell.Value = ConvertToSafeText(value);
                break;
        }
    }
}
