namespace Prismedia.Domain.Entities;

/// <summary>
/// Final result of one Files upload after its conflict policy was applied.
/// </summary>
public enum FileUploadOutcome {
    /// <summary>The file was stored at the requested path.</summary>
    [Code("uploaded")]
    Uploaded,

    /// <summary>The requested path was taken, so the file was stored under a numbered name.</summary>
    [Code("renamed")]
    Renamed,

    /// <summary>The requested path was taken and the file was not stored.</summary>
    [Code("skipped")]
    Skipped
}
