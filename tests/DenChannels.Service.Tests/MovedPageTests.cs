using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DenChannels.Service.Tests;

public sealed class MovedPageTests : IDisposable
{
    private readonly string _webRootPath;
    private readonly WebApplicationFactory<Program> _factory;

    public MovedPageTests()
    {
        _webRootPath = Path.Combine(Path.GetTempPath(), $"den-channels-moved-page-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_webRootPath);

        var fixtureIndex = Path.Combine(AppContext.BaseDirectory, "Fixtures", "moved-page", "index.html");
        File.Copy(fixtureIndex, Path.Combine(_webRootPath, "index.html"));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseWebRoot(_webRootPath));
    }

    [Fact]
    public async Task Root_ServesMovedPageHtml()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("Den Channels", body);
        Assert.Contains("Den Web", body);
        Assert.Contains("192.168.1.10:18080", body);
    }

    [Fact]
    public async Task Root_ServesBuiltInMovedPage_WhenIndexFileMissing()
    {
        var emptyWebRoot = Path.Combine(Path.GetTempPath(), $"den-channels-empty-webroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyWebRoot);
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseWebRoot(emptyWebRoot));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("text/html", response.Content.Headers.ContentType?.ToString());
            Assert.Contains("Den Channels", body);
            Assert.Contains("Den Web", body);
            Assert.Contains("192.168.1.10:18080", body);
        }
        finally
        {
            if (Directory.Exists(emptyWebRoot))
            {
                Directory.Delete(emptyWebRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApiMiss_ReturnsJson404_NotHtml()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/not-a-route");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("not_found", body);
        // Must not contain HTML markup
        Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthMiss_ReturnsJson404_NotHtml()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health/unknown-check");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DenCoreApiProxyMiss_DoesNotReturnHtml()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/den-core-api/api/nonexistent");
        var body = await response.Content.ReadAsStringAsync();

        // In a test environment without a real Den Core upstream the proxy
        // may return various content types. The key invariant is non-HTML body.
        Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonApiPublicPath_ServesMovedPage_WhenIndexFileExists()
    {
        using var client = _factory.CreateClient();

        // A public non-API path that doesn't match any registered route
        // falls through to the moved-page via MapFallback.
        using var response = await client.GetAsync("/some-unknown-public-path");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("Den Channels", body);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_webRootPath))
        {
            Directory.Delete(_webRootPath, recursive: true);
        }
    }
}
