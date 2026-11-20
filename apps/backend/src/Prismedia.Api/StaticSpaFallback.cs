using Prismedia.Contracts.Media;
using Prismedia.Api.Security;
using Prismedia.Contracts.Opds;

namespace Prismedia.Api;

/// <summary>
/// Serves the built Svelte shell for browser navigations that are not backend routes or files.
/// </summary>
internal static class StaticSpaFallback {
    internal static IApplicationBuilder UseStaticSpaFallback(
        this IApplicationBuilder app,
        string staticIndexPath,
        string? shellPolicy) =>
        app.Use(async (context, next) => {
            if (!ShouldServeSpaShell(context.Request)) {
                await next();
                return;
            }

            context.Response.ContentType = MediaContentTypes.Html;
            SetSpaShellHeaders(context.Response, shellPolicy);
            if (HttpMethods.IsHead(context.Request.Method)) {
                return;
            }

            await context.Response.SendFileAsync(staticIndexPath, context.RequestAborted);
        });

    /// <summary>
    /// Headers for every SPA shell response: never cached (the shell points at content-hashed
    /// chunks and must be revalidated on each navigation so PWA sessions pick up new builds),
    /// plus the shell Content-Security-Policy when one was built from the static index.
    /// </summary>
    internal static void SetSpaShellHeaders(HttpResponse response, string? shellPolicy) {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
        if (shellPolicy is not null) {
            response.Headers[SecurityHeaders.ContentSecurityPolicyHeader] = shellPolicy;
        }
    }

    internal static bool ShouldServeSpaShell(HttpRequest request) {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/assets", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments(OpdsProtocol.Prefix)) {
            return false;
        }

        if (Path.HasExtension(path)) {
            return false;
        }

        return request.Headers.Accept.Count == 0 ||
            request.Headers.Accept.Any(value =>
                value?.Contains(MediaContentTypes.Html, StringComparison.OrdinalIgnoreCase) == true);
    }
}
