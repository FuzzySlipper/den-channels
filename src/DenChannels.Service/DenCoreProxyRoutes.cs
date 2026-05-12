using DenChannels.Service.Configuration;
using Microsoft.Extensions.Options;

namespace DenChannels.Service;

public static class DenCoreProxyRoutes
{
    public static void MapDenCoreApiProxy(this WebApplication app)
    {
        var methods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };
        app.MapMethods("/den-core-api/{**path}", methods, ProxyAsync);
    }

    private static async Task ProxyAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        IOptions<DenChannelsOptions> options,
        CancellationToken cancellationToken)
    {
        var path = (string?)context.Request.RouteValues["path"] ?? string.Empty;
        var targetUri = BuildTargetUri(options.Value.DenCore.BaseUrl, path, context.Request.QueryString);

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
        CopyRequestHeaders(context, request, options.Value.ServiceAuth.ServiceToken);

        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();

        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static Uri BuildTargetUri(string baseUrl, string path, QueryString queryString)
    {
        var builder = new UriBuilder(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'))
        {
            Query = queryString.HasValue ? queryString.Value!.TrimStart('?') : string.Empty
        };

        return builder.Uri;
    }

    private static void CopyRequestHeaders(HttpContext context, HttpRequestMessage request, string? serviceToken)
    {
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(serviceToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
    }
}
