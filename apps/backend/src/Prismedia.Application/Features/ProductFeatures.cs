using Prismedia.Domain.Entities;

namespace Prismedia.Application.Features;

/// <summary>
/// Which optional product areas this install runs. Read once at startup from the configured
/// <see cref="FeatureProfile"/>; pages, navigation, and background work consult it.
/// </summary>
public interface IProductFeatures {
    /// <summary>Configured profile.</summary>
    FeatureProfile Profile { get; }

    /// <summary>Enabled optional areas, in declaration order.</summary>
    IReadOnlyList<ProductFeature> Enabled { get; }

    /// <summary>Whether an optional area is on.</summary>
    bool IsEnabled(ProductFeature feature);
}

/// <summary>Immutable feature set derived from a <see cref="FeatureProfile"/>.</summary>
public sealed class ProductFeatureSet : IProductFeatures {
    private readonly HashSet<ProductFeature> enabled;

    /// <summary>Creates the feature set for a profile.</summary>
    public ProductFeatureSet(FeatureProfile profile) {
        Profile = profile;
        enabled = profile == FeatureProfile.Full ? [.. Enum.GetValues<ProductFeature>()] : [];
        Enabled = Enum.GetValues<ProductFeature>().Where(enabled.Contains).ToArray();
    }

    /// <inheritdoc />
    public FeatureProfile Profile { get; }

    /// <inheritdoc />
    public IReadOnlyList<ProductFeature> Enabled { get; }

    /// <inheritdoc />
    public bool IsEnabled(ProductFeature feature) => enabled.Contains(feature);

    /// <summary>
    /// Parses a configured profile code. Missing values select <see cref="FeatureProfile.Simple"/>;
    /// unknown values are rejected so a typo cannot silently switch features on or off.
    /// </summary>
    /// <param name="configured">Profile code from configuration, such as <c>simple</c> or <c>full</c>.</param>
    public static FeatureProfile ParseProfile(string? configured) {
        if (string.IsNullOrWhiteSpace(configured)) {
            return FeatureProfile.Simple;
        }

        return configured.Trim().ToLowerInvariant().TryDecodeAs<FeatureProfile>(out var profile)
            ? profile
            : throw new InvalidOperationException(
                $"Unknown feature profile '{configured}'. Use '{FeatureProfile.Simple.ToCode()}' or '{FeatureProfile.Full.ToCode()}'.");
    }
}

/// <summary>
/// Maps background job types to the optional area that owns them. Jobs of a switched-off area are
/// never started, so hidden features make no metadata, plugin, or acquisition calls.
/// </summary>
public static class ProductFeatureJobPolicy {
    private static readonly Dictionary<JobType, ProductFeature> Owners = new() {
        [JobType.ScanBook] = ProductFeature.Books,
        [JobType.ScanComic] = ProductFeature.Books,
        [JobType.GenerateBookCoverThumbnail] = ProductFeature.Books,
        [JobType.MapBookChapters] = ProductFeature.Books,
        [JobType.ScanAudio] = ProductFeature.Audio,
        [JobType.ProbeAudio] = ProductFeature.Audio,
        [JobType.FingerprintAudio] = ProductFeature.Audio,
        [JobType.GenerateAudioWaveform] = ProductFeature.Audio,
        [JobType.UpdatePlugins] = ProductFeature.Plugins,
        [JobType.AcquireSubtitles] = ProductFeature.Identify,
        [JobType.AcquireSubtitle] = ProductFeature.Identify,
        [JobType.RefreshEntity] = ProductFeature.Identify,
        [JobType.IdentifySearch] = ProductFeature.Identify,
        [JobType.IdentifyProviderCall] = ProductFeature.Identify,
        [JobType.IdentifyApply] = ProductFeature.Identify,
        [JobType.BulkIdentify] = ProductFeature.Identify,
        [JobType.AutoIdentify] = ProductFeature.Identify,
        [JobType.IdentifyCascade] = ProductFeature.Identify,
        [JobType.IntegrationTransfer] = ProductFeature.Acquisition,
        [JobType.ManagedLibraryReconcile] = ProductFeature.Acquisition,
        [JobType.ManagedControl] = ProductFeature.Acquisition,
        [JobType.AcquisitionSearch] = ProductFeature.Acquisition,
        [JobType.AcquisitionMonitor] = ProductFeature.Acquisition,
        [JobType.AcquisitionImport] = ProductFeature.Acquisition,
        [JobType.AcquisitionFinalize] = ProductFeature.Acquisition,
        [JobType.AcquisitionFailedHandle] = ProductFeature.Acquisition,
        [JobType.MonitoredSearch] = ProductFeature.Acquisition,
        [JobType.AcquisitionUpgradeReplace] = ProductFeature.Acquisition,
        [JobType.AcquisitionEnrich] = ProductFeature.Acquisition,
        [JobType.RequestAcquisitionFanout] = ProductFeature.Acquisition,
    };

    /// <summary>The optional area that owns a job type, or null when the job is part of the core library.</summary>
    public static ProductFeature? OwnerOf(JobType type) => Owners.TryGetValue(type, out var owner) ? owner : null;

    /// <summary>Whether a job of this type may run under the given feature set.</summary>
    public static bool IsAllowed(IProductFeatures features, JobType type) =>
        OwnerOf(type) is not { } owner || features.IsEnabled(owner);
}
