namespace Prismedia.Domain.Entities;

/// <summary>
/// Optional product areas that a feature profile can switch off. Switching an area off hides its
/// pages and navigation and stops its background work, but never deletes its data, so turning it
/// back on restores everything as it was.
/// </summary>
public enum ProductFeature {
    /// <summary>Movies, TV series, seasons, and episodes as their own browse pages.</summary>
    [Code("movies-series")]
    MoviesAndSeries,

    /// <summary>Music albums, artists, and tracks.</summary>
    [Code("audio")]
    Audio,

    /// <summary>Books, ebooks, audiobooks, comics, and authors.</summary>
    [Code("books")]
    Books,

    /// <summary>Studios as a browse dimension.</summary>
    [Code("studios")]
    Studios,

    /// <summary>The release calendar.</summary>
    [Code("calendar")]
    Calendar,

    /// <summary>Requests, downloads, connected managers, and every acquisition background task.</summary>
    [Code("acquisition")]
    Acquisition,

    /// <summary>Plugin management, scrapers, and automatic plugin updates.</summary>
    [Code("plugins")]
    Plugins,

    /// <summary>Online metadata: Identify, automatic identification, metadata refresh, and subtitle downloads.</summary>
    [Code("identify")]
    Identify,

    /// <summary>Consumption statistics pages.</summary>
    [Code("stats")]
    Stats,

    /// <summary>Developer and design-lab pages.</summary>
    [Code("developer-tools")]
    DeveloperTools
}
