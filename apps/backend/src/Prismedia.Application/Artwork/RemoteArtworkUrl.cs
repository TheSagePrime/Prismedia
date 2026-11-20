using Prismedia.Contracts.Plugins;

namespace Prismedia.Application.Artwork;

/// <summary>
/// Shared policy for artwork references that arrive from metadata proposals, plugins, and client
/// review payloads. Such references are only ever remote images: they are downloaded into the cache or
/// rendered by the browser, never resolved against the server filesystem. Accepting only absolute
/// http(s) URLs here keeps proposal artwork from being persisted as a local path.
/// </summary>
public static class RemoteArtworkUrl {
    /// <summary>Longest artwork URL accepted from a proposal.</summary>
    public const int MaxLength = 8192;

    /// <summary>
    /// Returns true when <paramref name="value"/> is an absolute http or https URL with a host, no
    /// embedded credentials, and no whitespace, control characters, or backslashes.
    /// </summary>
    /// <param name="value">Candidate artwork URL.</param>
    public static bool IsAllowed(string? value) => TryParse(value, out _);

    /// <summary>
    /// Returns true for a blank reference (which every consumer skips) or an allowed remote URL.
    /// </summary>
    /// <param name="value">Candidate artwork URL.</param>
    public static bool IsAllowedOrBlank(string? value) => string.IsNullOrWhiteSpace(value) || IsAllowed(value);

    /// <summary>Parses an allowed artwork URL; see <see cref="IsAllowed"/> for the rules.</summary>
    /// <param name="value">Candidate artwork URL.</param>
    /// <param name="uri">The parsed absolute URI when allowed.</param>
    /// <returns>True when the value is an allowed remote artwork URL.</returns>
    public static bool TryParse(string? value, out Uri uri) {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxLength
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.Host.Length == 0
            || parsed.UserInfo.Length > 0) {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// Returns true when every artwork URL in the proposal trees (images on each node, structural
    /// children, and relationships) and every non-blank selected image is an allowed remote URL. Blank
    /// references are ignored because every consumer already skips them.
    /// </summary>
    /// <param name="proposals">Proposal roots to inspect; null entries are ignored.</param>
    /// <param name="selectedImages">Optional role-to-URL selections; null values mean "clear".</param>
    public static bool AllAllowed(
        IEnumerable<EntityMetadataProposal?> proposals,
        IReadOnlyDictionary<string, string?>? selectedImages = null) {
        if (selectedImages is not null
            && !selectedImages.Values.All(IsAllowedOrBlank)) {
            return false;
        }

        var pending = new Stack<EntityMetadataProposal>(proposals.OfType<EntityMetadataProposal>());
        while (pending.TryPop(out var proposal)) {
            if (!(proposal.Images ?? []).All(image => IsAllowedOrBlank(image?.Url))) {
                return false;
            }

            foreach (var child in (proposal.Children ?? []).Concat(proposal.Relationships ?? [])) {
                if (child is not null) {
                    pending.Push(child);
                }
            }
        }

        return true;
    }
}
