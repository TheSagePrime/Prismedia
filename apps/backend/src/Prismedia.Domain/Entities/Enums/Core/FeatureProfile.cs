namespace Prismedia.Domain.Entities;

/// <summary>
/// Named set of enabled <see cref="ProductFeature"/> areas, chosen once per install with the
/// <c>PRISMEDIA_FEATURE_PROFILE</c> setting.
/// </summary>
public enum FeatureProfile {
    /// <summary>
    /// A private photo and video library: folders, images, videos, people, tags, and collections.
    /// Every optional area is off. This is the default when no profile is configured.
    /// </summary>
    [Code("simple")]
    Simple,

    /// <summary>Every product area is on.</summary>
    [Code("full")]
    Full
}
