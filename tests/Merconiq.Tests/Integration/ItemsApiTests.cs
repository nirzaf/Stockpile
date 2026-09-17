using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public class ItemsApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ItemsApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AuthClient => _factory.CreateAuthenticatedClient();
    private HttpClient AnonClient => _factory.CreateUnauthenticatedClient();

    private async Task<Item> SeedItemAsync(string itemCode = "TEST-001", decimal rate = 10m)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = new Item { ItemCode = itemCode, Description = "Test Item", Rate = rate };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private async Task<int> SeedCompanyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var company = new Company
        {
            Code = $"IMP-{Guid.NewGuid():N}"[..12],
            LegalName = "Import company",
            BaseCurrency = "QAR",
            CurrencyScale = 2
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private async Task GrantViewerAccessAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var viewer = (await users.FindByNameAsync("viewer@test-tenant.test"))!;
        var company = new Company
        {
            Code = $"VIEW-{Guid.NewGuid():N}"[..12],
            LegalName = "Viewer company"
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = company.Id,
            UserId = viewer.Id,
            Capabilities = CompanyCapability.View,
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAll_Authenticated_Returns200WithItems()
    {
        var client = AuthClient;
        await SeedItemAsync();

        var response = await client.GetAsync("/api/v1/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAll_WithPageSize_ReturnsOnlyRequestedItemsAndTotalCount()
    {
        var client = AuthClient;
        await SeedItemAsync($"PAGE-{Guid.NewGuid():N}".Substring(0, 15));
        await SeedItemAsync($"PAGE-{Guid.NewGuid():N}".Substring(0, 15));

        var response = await client.GetAsync("/api/v1/items?page=1&pageSize=1");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(1);
        body.GetProperty("data").GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetAll_Unauthenticated_Returns401OrRedirect()
    {
        var client = AnonClient;

        var response = await client.GetAsync("/api/v1/items");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task GetById_ExistingItem_Returns200()
    {
        var client = AuthClient;
        var item = await SeedItemAsync("BYID-001");

        var response = await client.GetAsync($"/api/v1/items/{item.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetById_NonExistingItem_Returns404()
    {
        var client = AuthClient;

        var response = await client.GetAsync("/api/v1/items/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_WithQuery_Returns200()
    {
        var client = AuthClient;
        await SeedItemAsync("SEARCH-001");

        var response = await client.GetAsync("/api/v1/items/search?q=SEARCH");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_ValidItem_Returns201WithLocation()
    {
        var client = AuthClient;

        var command = new { ItemCode = $"CREATE-{Guid.NewGuid():N}".Substring(0, 20), Description = "Created item", Rate = 15.50m };
        var response = await client.PostAsJsonAsync("/api/v1/items", command);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task ItemReads_Viewer_returns200()
    {
        var client = _factory.CreateAuthenticatedClient("Viewer");
        await GrantViewerAccessAsync();
        var item = await SeedItemAsync($"VIEWER-{Guid.NewGuid():N}".Substring(0, 20));

        var responses = new[]
        {
            await client.GetAsync("/api/v1/items"),
            await client.GetAsync($"/api/v1/items/{item.Id}"),
            await client.GetAsync($"/api/v1/items/search?q={item.ItemCode}")
        };

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ItemCreate_Viewer_is_forbidden()
    {
        var client = _factory.CreateAuthenticatedClient("Viewer");
        await GrantViewerAccessAsync();

        var response = await client.PostAsJsonAsync("/api/v1/items", new
        {
            ItemCode = "VIEWER-001",
            Description = "Must be rejected",
            Rate = 1m
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ItemUpdate_Viewer_is_forbidden()
    {
        var client = _factory.CreateAuthenticatedClient("Viewer");
        await GrantViewerAccessAsync();

        var response = await client.PutAsJsonAsync("/api/v1/items/1", new
        {
            Id = 1,
            Description = "Must be rejected",
            Rate = 1m,
            SupplierId = (int?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ItemDelete_Viewer_is_forbidden()
    {
        var client = _factory.CreateAuthenticatedClient("Viewer");
        await GrantViewerAccessAsync();

        var response = await client.DeleteAsync("/api/v1/items/1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_InvalidItem_Returns400()
    {
        var client = AuthClient;

        var command = new { ItemCode = "", Description = "", Rate = 0m };
        var response = await client.PostAsJsonAsync("/api/v1/items", command);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ExistingItem_Returns204()
    {
        var client = AuthClient;
        var item = await SeedItemAsync("UPDATE-001");

        var command = new { Id = item.Id, Description = "Updated", Rate = 25m, SupplierId = (int?)null };
        var response = await client.PutAsJsonAsync($"/api/v1/items/{item.Id}", command);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Update_MismatchedId_Returns400()
    {
        var client = AuthClient;

        var command = new { Id = 999, Description = "Updated", Rate = 25m, SupplierId = (int?)null };
        var response = await client.PutAsJsonAsync("/api/v1/items/1", command);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ExistingItem_Returns204()
    {
        var client = AuthClient;
        var item = await SeedItemAsync("DELETE-001");

        var response = await client.DeleteAsync($"/api/v1/items/{item.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ItemImport_Viewer_is_forbidden()
    {
        var client = _factory.CreateAuthenticatedClient("Viewer");
        await GrantViewerAccessAsync();
        var request = new
        {
            Csv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\nitem-1,SKU-1,Widget,12.50,,,,1,1,2,false",
            DryRun = true
        };

        var response = await client.PostAsJsonAsync("/api/v1/organization/items/import", request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ItemImport_Admin_dry_run_returns200()
    {
        var client = AuthClient;
        var companyId = await SeedCompanyAsync();
        var request = new
        {
            Csv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\nitem-1,SKU-1,Widget,12.50,,,,1,1,2,false",
            DryRun = true,
            CompanyId = companyId
        };

        var response = await client.PostAsJsonAsync("/api/v1/organization/items/import", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ItemImport_Admin_without_company_scope_returns400()
    {
        var client = AuthClient;
        var request = new
        {
            Csv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\nitem-1,SKU-1,Widget,12.50,,,,1,1,2,false",
            DryRun = true
        };

        var response = await client.PostAsJsonAsync("/api/v1/organization/items/import", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
