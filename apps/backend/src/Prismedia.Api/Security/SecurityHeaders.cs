using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Prismedia.Contracts.Media;

namespace Prismedia.Api.Security;

/// <summary>
/// Response hardening headers: a global baseline applied to every response, the
/// Content-Security-Policy for the Svelte SPA shell, and a lockdown policy for
/// user-controlled file bytes (cache assets and entity file content) so a crafted
/// SVG/HTML/XML file opened directly cannot run script on the Prismedia origin.
/// </summary>
internal static partial class SecurityHeaders {
    internal const string ContentTypeOptionsHeader = "X-Content-Type-Options";
    internal const string ReferrerPolicyHeader = "Referrer-Policy";
    internal const string FrameOptionsHeader = "X-Frame-Options";
    internal const string ContentSecurityPolicyHeader = "Content-Security-Policy";

    internal const string NoSniff = "nosniff";

    /// <summary>
    /// Same-origin referrers only, so URLs carrying an <c>access_token</c> query are never
    /// leaked to third-party hosts through the Referer header.
    /// </summary>
    internal const string SameOriginReferrer = "same-origin";

    internal const string SameOriginFrames = "SAMEORIGIN";

    /// <summary>
    /// Policy for raw user-controlled bytes. Inert for <c>&lt;img&gt;</c>, media elements and
    /// fetch consumers; when such a file is navigated to directly it renders sandboxed with
    /// no script, no plugins and no subresources beyond inline styles and data: images.
    /// </summary>
    internal const string FileContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox";

    /// <summary>
    /// External script origin for the Google Cast sender framework the video player loads
    /// on demand.
    /// </summary>
    private const string CastSenderScriptSource = "https://www.gstatic.com/cv/js/sender/";

    private const string CastFrameworkScriptSource = "https://www.gstatic.com/cast/sdk/libs/";

    private const string AudioTypePrefix = "audio/";
    private const string VideoTypePrefix = "video/";

    private const string CastChromeScriptSource = "https://www.gstatic.com/eureka/clank/";

    /// <summary>
    /// Adds baseline headers (nosniff, same-origin referrer, same-origin framing) to every
    /// response unless an endpoint already set them.
    /// </summary>
    internal static IApplicationBuilder UsePrismediaSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) => {
            context.Response.OnStarting(static state => {
                var headers = ((HttpContext)state).Response.Headers;
                headers.TryAdd(ContentTypeOptionsHeader, NoSniff);
                headers.TryAdd(ReferrerPolicyHeader, SameOriginReferrer);
                headers.TryAdd(FrameOptionsHeader, SameOriginFrames);
                return Task.CompletedTask;
            }, context);
            return next();
        });

    /// <summary>Applies the file lockdown policy to a response carrying user-controlled bytes.</summary>
    internal static void ApplyFileContentPolicy(HttpResponse response) {
        response.Headers[ContentTypeOptionsHeader] = NoSniff;
        response.Headers[ContentSecurityPolicyHeader] = FileContentSecurityPolicy;
    }

    /// <summary>
    /// Whether a file response of <paramref name="contentType"/> should carry the sandbox
    /// lockdown policy. PDFs, audio and video are excluded: they cannot run script as a
    /// top-level document, and browsers refuse to run their built-in PDF viewer or media
    /// player inside a sandboxed (opaque-origin) document. Every other type is either inert
    /// (raster images, archives) or potentially active (SVG, HTML, XML, unknown) and is locked.
    /// </summary>
    internal static bool ShouldLockDownFileContent(string? contentType) {
        var mediaType = MediaTypeOf(contentType);
        return !(string.Equals(mediaType, MediaContentTypes.Pdf, StringComparison.OrdinalIgnoreCase) ||
            mediaType.StartsWith(AudioTypePrefix, StringComparison.OrdinalIgnoreCase) ||
            mediaType.StartsWith(VideoTypePrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the SPA shell Content-Security-Policy for the given built <c>index.html</c>.
    /// Inline bootstrap scripts emitted by SvelteKit are allowed by their SHA-256 hash, so
    /// the policy tracks each build without trusting arbitrary inline script. The policy
    /// deliberately restricts only script, worker, plugin, base and framing sources: book
    /// documents rendered in same-origin blob: frames inherit it, and they still need their
    /// own blob: stylesheets, fonts and images.
    /// </summary>
    internal static string BuildSpaShellPolicy(string indexHtml) {
        var scriptSources = new List<string> { "'self'" };
        foreach (Match match in InlineScriptPattern().Matches(indexHtml)) {
            var body = match.Groups["body"].Value.Replace("\r\n", "\n");
            if (body.Length == 0) {
                continue;
            }

            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            var source = $"'sha256-{hash}'";
            if (!scriptSources.Contains(source)) {
                scriptSources.Add(source);
            }
        }

        // WebAssembly is compiled by the ASS subtitle renderer and pdf.js decoders.
        scriptSources.Add("'wasm-unsafe-eval'");
        scriptSources.Add(CastSenderScriptSource);
        scriptSources.Add(CastFrameworkScriptSource);
        scriptSources.Add(CastChromeScriptSource);

        return string.Join("; ",
            $"script-src {string.Join(' ', scriptSources)}",
            "worker-src 'self' blob:",
            "object-src 'none'",
            "base-uri 'self'",
            "frame-ancestors 'self'");
    }

    /// <summary>Reads <paramref name="indexPath"/> and builds its shell policy, or null when absent.</summary>
    internal static string? TryBuildSpaShellPolicy(string indexPath) =>
        File.Exists(indexPath) ? BuildSpaShellPolicy(File.ReadAllText(indexPath)) : null;

    private static string MediaTypeOf(string? contentType) {
        if (string.IsNullOrWhiteSpace(contentType)) {
            return string.Empty;
        }

        var separator = contentType.IndexOf(';');
        return (separator >= 0 ? contentType[..separator] : contentType).Trim();
    }

    [GeneratedRegex(@"<script\b(?![^>]*\bsrc\s*=)[^>]*>(?<body>[\s\S]*?)</script\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptPattern();
}
