using Prismedia.Api.Security;
using Prismedia.Application.Artwork;
using Prismedia.Application.Entities;
using Prismedia.Contracts.Entities;
using Prismedia.Contracts.System;
using Prismedia.Domain.Entities;

namespace Prismedia.Api.Endpoints;

internal static class EntityDetailEndpoint {
    internal static RouteGroupBuilder MapEntityDetailEndpoint(this RouteGroupBuilder group) {
        group.MapPatch("/{kind}/{id:guid}", async (
            string kind,
            Guid id,
            EntityMetadataUpdateRequest request,
            HttpContext httpContext,
            IEntityMetadataPatchService metadata,
            IEntityReadService entities,
            CancellationToken cancellationToken) =>
            await PatchEntityAsync(id, kind, request, httpContext, metadata, entities, cancellationToken))
            .WithName("UpdateEntityByKind")
            .WithSummary("Update Entity By Kind.")
            .Produces<EntityCard>()
            .Produces<ApiProblem>(StatusCodes.Status400BadRequest)
            .Produces<ApiProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}", async (
            Guid id,
            bool? hideNsfw,
            HttpContext httpContext,
            IEntityReadService entities,
            CancellationToken cancellationToken) =>
            await EntityEndpointResults.GetEntityAsync(
                id,
                NsfwVisibility.ShouldHide(hideNsfw, httpContext),
                entities,
                cancellationToken))
            .WithName("GetEntity")
            .WithSummary("Get Entity.")
            .Produces<EntityCard>()
            .Produces<ApiProblem>(StatusCodes.Status404NotFound);

        group.MapPatch("/{id:guid}", async (
            Guid id,
            EntityMetadataUpdateRequest request,
            HttpContext httpContext,
            IEntityMetadataPatchService metadata,
            IEntityReadService entities,
            CancellationToken cancellationToken) =>
            await PatchEntityAsync(id, expectedKind: null, request, httpContext, metadata, entities, cancellationToken))
            .WithName("UpdateEntity")
            .WithSummary("Update Entity.")
            .Produces<EntityCard>()
            .Produces<ApiProblem>(StatusCodes.Status400BadRequest)
            .Produces<ApiProblem>(StatusCodes.Status404NotFound);

        return group;
    }

    /// <summary>
    /// Applies a scoped metadata patch. Admins may patch any entity. Other users may only
    /// patch collections they own, and only through plain field edits: an entity hidden from
    /// them (library access, NSFW ceiling, collection visibility) reads as missing, and any
    /// other visible entity is forbidden.
    /// </summary>
    internal static async Task<IResult> PatchEntityAsync(
        Guid id,
        string? expectedKind,
        EntityMetadataUpdateRequest request,
        HttpContext httpContext,
        IEntityMetadataPatchService metadata,
        IEntityReadService entities,
        CancellationToken cancellationToken) {
        var user = httpContext.GetCurrentUser();
        if (user is null) {
            return Results.Json(
                new ApiProblem(ApiProblemCodes.AuthenticationRequired, "Authentication is required."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var isAdmin = user.Role == UserRole.Admin;
        // Non-admins are bounded by their NSFW ceiling, not the per-browser display mode.
        var hideNsfw = !isAdmin && !user.AllowNsfw;
        if (!isAdmin) {
            var target = await entities.GetAsync(id, hideNsfw, cancellationToken);
            if (target is null) {
                return EntityNotFound(id);
            }

            if (target.Kind != EntityKind.Collection || HasNonFieldEdits(request)) {
                return Results.Json(
                    new ApiProblem(ApiProblemCodes.AdminRequired, "Administrator access is required."),
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        if (!RemoteArtworkUrl.AllAllowed(
                (request.Children ?? []).Concat(request.Relationships ?? []),
                request.SelectedImages)) {
            return Results.BadRequest(new ApiProblem(
                ApiProblemCodes.InvalidEntityMetadataPatch,
                "Artwork must be an absolute http or https URL."));
        }

        EntityMetadataPatchResult result;
        try {
            result = await metadata.ApplyPatchAsync(id, request, expectedKind, cancellationToken);
        } catch (ArgumentException ex) {
            return Results.BadRequest(new ApiProblem(ApiProblemCodes.InvalidEntityMetadataPatch, ex.Message));
        }

        if (result is EntityMetadataPatchResult.NotFound or EntityMetadataPatchResult.KindMismatch) {
            return EntityNotFound(id);
        }

        return expectedKind is null
            ? await EntityEndpointResults.GetEntityAsync(id, hideNsfw, entities, cancellationToken)
            : await EntityEndpointResults.GetEntityAsync(id, expectedKind, hideNsfw, entities, cancellationToken);
    }

    /// <summary>
    /// True when the request carries more than scoped field edits (artwork selections or nested
    /// child/relationship proposals); those remain admin-only.
    /// </summary>
    private static bool HasNonFieldEdits(EntityMetadataUpdateRequest request) =>
        request.SelectedImages is { Count: > 0 } ||
        request.Children is { Count: > 0 } ||
        request.Relationships is { Count: > 0 };

    private static IResult EntityNotFound(Guid id) =>
        Results.NotFound(new ApiProblem(ApiProblemCodes.EntityNotFound, $"Entity '{id}' was not found."));
}
