using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Merconiq.Web.Services;

namespace Merconiq.Tests.Web.Services;

public class ExportFileBuilderTests
{
    [Theory]
    [InlineData("=")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("@")]
    public void Csv_neutralizes_formula_prefixes(string prefix)
    {
        var value = $"{prefix}SUM(A1:A2)";

        var csv = Encoding.UTF8.GetString(
            ExportFileBuilder.CreateCsv(["Value"], [[value]]));

        csv.Should().Contain($"\"'{value}\"");
    }

    [Theory]
    [InlineData("=")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("@")]
    public void Excel_neutralizes_formula_prefixes(string prefix)
    {
        var value = $"{prefix}SUM(A1:A2)";

        using var workbook = new XLWorkbook(
            new MemoryStream(ExportFileBuilder.CreateExcel("Export", ["Value"], [[value]])));

        var cell = workbook.Worksheet("Export").Cell(2, 1);

        cell.Value.Type.Should().Be(XLDataType.Text);
        cell.HasFormula.Should().BeFalse();
        cell.GetString().Should().Be(value);
    }

    [Fact]
    public void Csv_and_excel_preserve_ordinary_text_and_numeric_values()
    {
        var csv = Encoding.UTF8.GetString(
            ExportFileBuilder.CreateCsv(["Value"], [["ordinary text", -42]]));

        csv.Should().Contain("\"ordinary text\",\"-42\"");

        using var workbook = new XLWorkbook(
            new MemoryStream(ExportFileBuilder.CreateExcel("Export", ["Value"], [["ordinary text", -42]])));
        var worksheet = workbook.Worksheet("Export");

        worksheet.Cell(2, 1).GetString().Should().Be("ordinary text");
        worksheet.Cell(2, 2).Value.Type.Should().Be(XLDataType.Number);
        worksheet.Cell(2, 2).GetDouble().Should().Be(-42);
    }
}
