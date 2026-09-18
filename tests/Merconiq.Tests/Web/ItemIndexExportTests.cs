using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Web.Components.Pages.Items;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Moq;

namespace Merconiq.Tests.Web;

public sealed class ItemIndexExportTests
{
    [Fact]
    public async Task Csv_and_excel_exports_require_a_current_company_view_grant()
    {
        var principal = CreatePrincipal();
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.HasCompanyCapabilityAsync(
                principal, CompanyCapability.View))
            .ReturnsAsync(true);

        var js = new RecordingJsRuntime();
        var component = CreateComponent(principal, authorization.Object, js);
        SetField(component, "_items", new List<Item> { CreateItem("AUTHORIZED-ITEM") });

        await InvokeHandlerAsync(component, "ExportToCsv");
        await InvokeHandlerAsync(component, "ExportToExcel");

        js.Downloads.Should().HaveCount(2);
        var csv = Encoding.UTF8.GetString((byte[])js.Downloads[0].Arguments![2]!);
        csv.Should().Contain("AUTHORIZED-ITEM");

        using var workbookStream = new MemoryStream((byte[])js.Downloads[1].Arguments![2]!);
        using var workbook = new ZipArchive(workbookStream, ZipArchiveMode.Read);
        string.Join('\n', workbook.Entries.Select(ReadEntry)).Should().Contain("AUTHORIZED-ITEM");

        authorization.Verify(service => service.HasCompanyCapabilityAsync(
            principal, CompanyCapability.View), Times.Exactly(2));
    }

    [Fact]
    public async Task Csv_and_excel_exports_do_not_download_when_current_view_authorization_is_denied()
    {
        var principal = CreatePrincipal();
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.HasCompanyCapabilityAsync(
                principal, CompanyCapability.View))
            .ReturnsAsync(false);

        var js = new RecordingJsRuntime();
        var component = CreateComponent(principal, authorization.Object, js);
        SetField(component, "_items", new List<Item> { CreateItem("STALE-ITEM") });

        await InvokeHandlerAsync(component, "ExportToCsv");
        await InvokeHandlerAsync(component, "ExportToExcel");

        js.Downloads.Should().BeEmpty();
        authorization.Verify(service => service.HasCompanyCapabilityAsync(
            principal, CompanyCapability.View), Times.Exactly(2));
    }

    private static ItemIndex CreateComponent(
        ClaimsPrincipal principal,
        ICurrentUserAuthorization authorization,
        IJSRuntime js)
    {
        var component = new ItemIndex();
        SetProperty(component, "AuthenticationStateProvider", new FixedAuthenticationStateProvider(principal));
        SetProperty(component, "CurrentUserAuthorization", authorization);
        SetProperty(component, "Snackbar", Mock.Of<ISnackbar>());
        SetProperty(component, "JS", js);
        return component;
    }

    private static ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "item-export-user"), new Claim("tenant_id", "tenant-a")], "test"));

    private static Item CreateItem(string itemCode) => new()
    {
        ItemCode = itemCode,
        Description = $"{itemCode} description",
        Rate = 12.5m,
        ReorderLevel = 3
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

    private static Task InvokeHandlerAsync(ItemIndex component, string name)
    {
        var method = typeof(ItemIndex).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
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
