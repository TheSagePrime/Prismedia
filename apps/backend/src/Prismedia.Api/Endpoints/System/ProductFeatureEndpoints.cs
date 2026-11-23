using Prismedia.Application.Features;
using Prismedia.Contracts.System;

namespace Prismedia.Api.Endpoints;

internal static class ProductFeatureEndpoints {
    internal static IEndpointRouteBuilder MapProductFeatureEndpoints(this IEndpointRouteBuilder routes) {
        routes.MapGet("/api/features", (IProductFeatures features) =>
                TypedResults.Ok(new ProductFeaturesResponse(features.Profile, features.Enabled)))
            .WithName("GetProductFeatures")
            .WithTags("System")
            .WithSummary("Lists the optional product areas this install runs.");

        return routes;
    }
}
