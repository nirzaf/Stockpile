using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Web.Components.Pages.Stock;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Moq;

namespace Merconiq.Tests.Web;

public sealed class StockIndexExportTests
{
    [Fact]
    public async Task Csv_and_excel_exports_use_fresh_rows_and_current_company_grants()
    {
        var principal = CreatePrincipal();
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.IsTenantAdministratorAsync(principal)).ReturnsAsync(false);
        authorization.Setup(service => service.GetAccessibleCompanyIdsAsync(
                principal, CompanyCapability.View))
            .ReturnsAsync((IReadOnlySet<int>)new HashSet<int> { 17 });

        var currentStock = CreateStock("CURRENT-ITEM", 5);
        var stockService = new Mock<IStockService>();
        stockService.Setup(service => service.GetForCompaniesAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 17 }))))
            .ReturnsAsync([currentStock]);

        var js = new RecordingJsRuntime();
        var component = CreateComponent(principal, authorization.Object, stockService.Object, js);
        SetField(component, "_stocks", new[] { CreateStock("STALE-ITEM", 999) });

        await InvokeHandlerAsync(component, "ExportToCsv");
        await InvokeHandlerAsync(component, "ExportToExcel");

        js.Downloads.Should().HaveCount(2);
        var csv = Encoding.UTF8.GetString((byte[])js.Downloads[0].Arguments![2]!);
        csv.Should().Contain("CURRENT-ITEM").And.Contain(",\"5\",").And.NotContain("STALE-ITEM");

        using var workbookStream = new MemoryStream((byte[])js.Downloads[1].Arguments![2]!);
        using var workbook = new ZipArchive(workbookStream, ZipArchiveMode.Read);
        var workbookXml = string.Join('\n', workbook.Entries.Select(ReadEntry));
        workbookXml.Should().Contain("CURRENT-ITEM").And.NotContain("STALE-ITEM");

        stockService.Verify(service => service.GetForCompaniesAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 17 }))), Times.Exactly(2));
        authorization.Verify(service => service.GetAccessibleCompanyIdsAsync(
            principal, CompanyCapability.View), Times.Exactly(2));
    }

    [Fact]
    public async Task Csv_and_excel_exports_do_not_download_after_view_grant_is_revoked()
    {
        var principal = CreatePrincipal();
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.IsTenantAdministratorAsync(principal)).ReturnsAsync(false);
        authorization.Setup(service => service.GetAccessibleCompanyIdsAsync(
                principal, CompanyCapability.View))
            .ReturnsAsync((IReadOnlySet<int>)new HashSet<int>());
        var stockService = new Mock<IStockService>();
        var js = new RecordingJsRuntime();
        var component = CreateComponent(principal, authorization.Object, stockService.Object, js);
        SetField(component, "_stocks", new[] { CreateStock("STALE-ITEM", 999) });

        await InvokeHandlerAsync(component, "ExportToCsv");
        await InvokeHandlerAsync(component, "ExportToExcel");

        js.Downloads.Should().BeEmpty();
        stockService.Verify(service => service.GetForCompaniesAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Never);
        stockService.Verify(service => service.GetAllAsync(), Times.Never);
    }

    private static StockIndex CreateComponent(
        ClaimsPrincipal principal,
        ICurrentUserAuthorization authorization,
        IStockService stockService,
        IJSRuntime js)
    {
        var component = new StockIndex();
        SetProperty(component, "StockService", stockService);
        SetProperty(component, "AuthenticationStateProvider", new FixedAuthenticationStateProvider(principal));
        SetProperty(component, "CurrentUserAuthorization", authorization);
        SetProperty(component, "Snackbar", Mock.Of<ISnackbar>());
        SetProperty(component, "JS", js);
        return component;
    }

    private static ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "stock-export-user"), new Claim("tenant_id", "tenant-a")], "test"));

    private static StockInHand CreateStock(string itemCode, int quantity) => new()
    {
        Item = new Item { ItemCode = itemCode, Description = $"{itemCode} description" },
        Location = new Location { Name = "Company warehouse" },
        Quantity = quantity
    };

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Injected property '{name}' was not found.");
        property.SetValue(target, value);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Component field '{name}' was not found.");
        field.SetValue(target, value);
    }

    private static Task InvokeHandlerAsync(StockIndex component, string name)
    {
        var method = typeof(StockIndex).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Export handler '{name}' was not found.");
        return (Task)method.Invoke(component, null)!;
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<(string Identifier, object?[]? Arguments)> Downloads { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Downloads.Add((identifier, args));
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Downloads.Add((identifier, args));
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
