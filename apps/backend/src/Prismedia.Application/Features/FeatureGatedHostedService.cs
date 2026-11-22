using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prismedia.Domain.Entities;

namespace Prismedia.Application.Features;

/// <summary>
/// Starts a background service only when the product area that owns it is enabled, so a switched-off
/// area runs no polling loops or outside calls.
/// </summary>
/// <typeparam name="TService">Background service owned by <paramref name="feature"/>.</typeparam>
internal sealed class FeatureGatedHostedService<TService>(
    TService inner,
    IProductFeatures features,
    ProductFeature feature,
    ILogger<FeatureGatedHostedService<TService>> logger) : IHostedService
    where TService : class, IHostedService {
    private bool started;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken) {
        if (!features.IsEnabled(feature)) {
            logger.LogInformation(
                "{Service} is not started because {Feature} is turned off in the {Profile} profile.",
                typeof(TService).Name,
                feature.ToCode(),
                features.Profile.ToCode());
            return;
        }

        started = true;
        await inner.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) =>
        started ? inner.StopAsync(cancellationToken) : Task.CompletedTask;
}

/// <summary>Registration helpers for feature-owned background services.</summary>
internal static class FeatureGatedHostedServiceRegistration {
    /// <summary>Registers <typeparamref name="TService"/> as a background service that runs only while <paramref name="feature"/> is on.</summary>
    public static IServiceCollection AddFeatureGatedHostedService<TService>(
        this IServiceCollection services,
        ProductFeature feature)
        where TService : class, IHostedService {
        services.AddSingleton<TService>();
        services.AddHostedService(provider => new FeatureGatedHostedService<TService>(
            provider.GetRequiredService<TService>(),
            provider.GetRequiredService<IProductFeatures>(),
            feature,
            provider.GetRequiredService<ILogger<FeatureGatedHostedService<TService>>>()));
        return services;
    }
}
