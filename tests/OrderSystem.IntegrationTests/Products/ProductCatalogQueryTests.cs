using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Api;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Products;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ProductCatalogQueryTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task ListProducts_NameTie_UsesIdTieBreakerAndReturnsCorrectPageMetadata()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var marker = $"Stable-{Guid.NewGuid():N}";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        var createdAt = new DateTimeOffset(2026, 9, 17, 4, 0, 0, TimeSpan.Zero);
        foreach (var id in ids.Reverse())
        {
            dbContext.Products.Add(new(id, marker, "Description", CatalogStatus.Active, createdAt));
        }

        await dbContext.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();

        var firstPage = await service.ListProductsAsync(
            new(Page: 1, PageSize: 2, Search: marker, SortBy: "name", SortDirection: "asc"),
            CatalogVisibility.ActiveOnly,
            CancellationToken.None);
        var secondPage = await service.ListProductsAsync(
            new(Page: 2, PageSize: 2, Search: marker, SortBy: "name", SortDirection: "asc"),
            CatalogVisibility.ActiveOnly,
            CancellationToken.None);

        Assert.True(firstPage.IsSuccess);
        Assert.Equal(ids[..2], firstPage.Value!.Items.Select(product => product.Id));
        Assert.Equal(3, firstPage.Value.TotalCount);
        Assert.Equal(2, firstPage.Value.TotalPages);
        Assert.Equal(1, firstPage.Value.Page);
        Assert.Equal(2, firstPage.Value.PageSize);
        Assert.Equal(ids[2], Assert.Single(secondPage.Value!.Items).Id);
    }

    [Fact]
    public async Task ListProducts_SearchesVariantNameAndSkuCaseInsensitively()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var suffix = Guid.NewGuid().ToString("N", null)[..6];
        var product = new Product(
            Guid.NewGuid(),
            "Unrelated product name",
            "Description",
            CatalogStatus.Active,
            DateTimeOffset.UtcNow);
        dbContext.Products.Add(product);
        dbContext.ProductVariants.Add(new(
            Guid.NewGuid(),
            product.Id,
            $"SKU-{suffix}",
            $"Needle {suffix}",
            1m,
            CatalogStatus.Active,
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();

        var byVariantName = await service.ListProductsAsync(
            new(Search: $"needle {suffix}".ToUpperInvariant()),
            CatalogVisibility.ActiveOnly,
            CancellationToken.None);
        var bySku = await service.ListProductsAsync(
            new(Search: $"sku-{suffix}".ToLowerInvariant()),
            CatalogVisibility.ActiveOnly,
            CancellationToken.None);

        Assert.Equal(product.Id, Assert.Single(byVariantName.Value!.Items).Id);
        Assert.Equal(product.Id, Assert.Single(bySku.Value!.Items).Id);
    }

    [Fact]
    public async Task ListProducts_InactiveFilterRequiresAllVisibility()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var marker = $"Inactive-{Guid.NewGuid():N}";
        var inactive = new Product(Guid.NewGuid(), marker, "Description", CatalogStatus.Inactive, DateTimeOffset.UtcNow);
        dbContext.Products.Add(inactive);
        await dbContext.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();

        var publicResult = await service.ListProductsAsync(
            new(Search: marker, Status: "Inactive"),
            CatalogVisibility.ActiveOnly,
            CancellationToken.None);
        var adminResult = await service.ListProductsAsync(
            new(Search: marker, Status: "Inactive"),
            CatalogVisibility.All,
            CancellationToken.None);

        Assert.False(publicResult.IsSuccess);
        Assert.Equal("FORBIDDEN", publicResult.Error!.Code);
        Assert.Equal(inactive.Id, Assert.Single(adminResult.Value!.Items).Id);
    }

    [Fact]
    public async Task GetProductDetail_ActiveVisibilityHidesInactiveProductsAndVariants()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var activeProduct = new Product(Guid.NewGuid(), $"Active-{Guid.NewGuid():N}", "Description", CatalogStatus.Active, now);
        var inactiveProduct = new Product(Guid.NewGuid(), $"Inactive-{Guid.NewGuid():N}", "Description", CatalogStatus.Inactive, now);
        dbContext.Products.AddRange(activeProduct, inactiveProduct);
        dbContext.ProductVariants.AddRange(
            new(Guid.NewGuid(), activeProduct.Id, UniqueSku("ACT"), "Active variant", 1m, CatalogStatus.Active, now),
            new(Guid.NewGuid(), activeProduct.Id, UniqueSku("INA"), "Inactive variant", 1m, CatalogStatus.Inactive, now));
        await dbContext.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();

        var publicActive = await service.GetProductDetailAsync(activeProduct.Id, CatalogVisibility.ActiveOnly, CancellationToken.None);
        var publicInactive = await service.GetProductDetailAsync(inactiveProduct.Id, CatalogVisibility.ActiveOnly, CancellationToken.None);
        var adminActive = await service.GetProductDetailAsync(activeProduct.Id, CatalogVisibility.All, CancellationToken.None);
        var adminInactive = await service.GetProductDetailAsync(inactiveProduct.Id, CatalogVisibility.All, CancellationToken.None);

        Assert.Equal("Active variant", Assert.Single(publicActive.Value!.Variants).Name);
        Assert.Equal("PRODUCT_NOT_FOUND", publicInactive.Error!.Code);
        Assert.Equal(2, adminActive.Value!.Variants.Count);
        Assert.True(adminInactive.IsSuccess);
    }

    private static string UniqueSku(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = postgres.ConnectionString,
                    ["Jwt:SigningKey"] = AuthenticationApiTests.TestSigningKey
                })));
}
