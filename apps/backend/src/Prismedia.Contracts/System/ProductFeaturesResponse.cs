using Prismedia.Domain.Entities;

namespace Prismedia.Contracts.System;

/// <summary>
/// Optional product areas this install runs. The web app hides pages, navigation, home sections,
/// and search results for areas that are off.
/// </summary>
/// <param name="Profile">Configured feature profile.</param>
/// <param name="EnabledFeatures">Optional areas that are on.</param>
public sealed record ProductFeaturesResponse(FeatureProfile Profile, IReadOnlyList<ProductFeature> EnabledFeatures);
