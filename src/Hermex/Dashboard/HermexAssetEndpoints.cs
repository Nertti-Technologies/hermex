using System.Reflection;
using Hermex.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hermex.Dashboard;

/// <summary>
/// Serves the dashboard's CSS and JavaScript directly from embedded resources. This makes
/// Hermex fully self-contained — it never depends on the host application's static-file
/// configuration or on Razor Class Library static-web-asset wiring.
/// </summary>
internal static class HermexAssetEndpoints
{
    private const string ResourcePrefix = "Hermex.Assets.";
    private static readonly Assembly Assembly = typeof(HermexAssetEndpoints).Assembly;

    public static void MapHermexAssets(IEndpointRouteBuilder endpoints, HermexAuthorizationOptions authorization)
    {
        var assets = endpoints.MapGet(HermexRoutes.Base + "/assets/{**assetPath}",
            (string assetPath, HttpResponse response) =>
            {
                if (string.IsNullOrWhiteSpace(assetPath) || assetPath.Contains("..", StringComparison.Ordinal))
                    return Results.NotFound();

                var resourceName = ResourcePrefix + assetPath.Replace('/', '.');
                var stream = Assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    return Results.NotFound();

                response.Headers.CacheControl = "no-cache";
                return Results.Stream(stream, ContentTypeFor(assetPath));
            });

        // Embedded CSS/JS/icons hold no mail: tagged Viewer, but Hermex adds no requirement unless
        // the host asks (a host FallbackPolicy still applies).
        if (authorization.AllowAnonymousAssets)
            assets.WithMetadata(new HermexEndpointMetadata(HermexEndpointTier.Viewer));
        else
            assets.WithHermexTier(HermexEndpointTier.Viewer, authorization);
    }

    private static string ContentTypeFor(string path)
    {
        if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            return "text/css; charset=utf-8";
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return "text/javascript; charset=utf-8";
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return "image/svg+xml";
        return "application/octet-stream";
    }
}
