namespace Prismedia.Domain.Entities;

/// <summary>
/// What a Files upload does when a file or folder already exists at its destination path.
/// Chosen once per upload batch on the client and applied per file, so one existing name
/// never aborts the rest of the batch.
/// </summary>
public enum FileUploadConflictPolicy {
    /// <summary>Leave the existing item untouched and report the incoming file as skipped.</summary>
    [Code("skip")]
    Skip,

    /// <summary>Keep the existing item and store the incoming file under a free numbered name.</summary>
    [Code("keep-both")]
    KeepBoth,

    /// <summary>Reject the incoming file with a conflict problem; other files continue.</summary>
    [Code("fail")]
    Fail
}
