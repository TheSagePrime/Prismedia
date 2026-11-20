using Prismedia.Api.Security;
using Prismedia.Application.Security;
using Prismedia.Application.Settings;
using Prismedia.Contracts.Settings;
using Prismedia.Contracts.System;
using Prismedia.Domain.Entities;

namespace Prismedia.Api.Endpoints;

/// <summary>
/// Watched library-root management. Admins manage every root and its per-user access;
/// members with the create-libraries permission manage roots they created (creator-only
/// visibility by default) and read only summaries of roots they were granted.
/// </summary>
public static class LibraryEndpoints {
    public static RouteGroupBuilder MapLibraryEndpoints(this IEndpointRouteBuilder routes) {
        var group = routes.MapGroup("/api/libraries")
            .WithTags("Settings");

        group.MapGet("", async (
            HttpContext httpContext,
            SettingsService settings,
            ILibraryAccessStore access,
            CancellationToken cancellationToken) => {
            var user = httpContext.GetCurrentUser()!;
            var roots = await settings.ListLibraryRootsAsync(cancellationToken);
            if (user.Role == UserRole.Admin) {
                var accessByRoot = await access.GetAccessByRootAsync(cancellationToken);
                return Results.Ok(roots
                    .Select(root => root with {
                        AccessUserIds = accessByRoot.GetValueOrDefault(root.Id) ?? []
                    })
                    .ToArray());
            }

            if (!user.CanCreateLibraries) {
                return LibraryManagementForbidden();
            }

            // Members with the create permission manage only the roots they created
            // and never see per-user access lists.
            return Results.Ok(roots.Where(root => root.CreatedByUserId == user.Id).ToArray());
        })
            .WithName("ListLibraryRoots")
            .WithSummary("Lists watched media roots: every root with per-user access for admins, own-created roots for library creators.")
            .Produces<LibraryRoot[]>()
            .Produces<ApiProblem>(StatusCodes.Status403Forbidden);

        group.MapGet("/accessible", async (
            HttpContext httpContext,
            SettingsService settings,
            ICurrentUserContext currentUser,
            CancellationToken cancellationToken) => {
            var roots = await settings.ListLibraryRootsAsync(cancellationToken);
            var allowed = await currentUser.GetAllowedLibraryRootIdsAsync(cancellationToken);
            var hideNsfw = NsfwVisibility.ShouldHide(null, httpContext);
            return Results.Ok(roots
                .Where(root =>
                    root.Enabled &&
                    (!hideNsfw || !root.IsNsfw) &&
                    (allowed is null || allowed.Contains(root.Id)))
                .Select(root => new LibraryRootSummary(
                    root.Id,
                    root.Label,
                    root.ScanVideos,
                    root.ScanImages,
                    root.ScanAudio,
                    root.ScanBooks,
                    root.IsNsfw,
                    root.IsReadOnly))
                .ToArray());
        })
            .WithName("ListAccessibleLibraryRoots")
            .WithSummary("Lists the library roots the signed-in user can access.")
            .Produces<LibraryRootSummary[]>();

        group.MapGet("/browse", async (
            string? path,
            HttpContext httpContext,
            SettingsService settings,
            ICurrentUserContext currentUser,
            CancellationToken cancellationToken) => {
            if (!CanCreateLibraries(httpContext)) {
                return LibraryManagementForbidden();
            }

            // Library creators never see inside roots they cannot access.
            var hidden = await InaccessibleRootPathsAsync(httpContext, settings, currentUser, cancellationToken);
            var listing = await settings.BrowseLibraryPathAsync(path, cancellationToken, hidden);
            return listing is null ? LibraryManagementForbidden() : Results.Ok(listing);
        })
            .WithName("BrowseLibraryPath")
            .WithSummary("Browses local directories for watched-root selection.")
            .Produces<LibraryBrowseResponse>()
            .Produces<ApiProblem>(StatusCodes.Status403Forbidden);

        group.MapPost("", async (
            LibraryRootCreateRequest request,
            HttpContext httpContext,
            SettingsService settings,
            ILibraryAccessStore access,
            ICurrentUserContext currentUser,
            CancellationToken cancellationToken) => {
            var user = httpContext.GetCurrentUser()!;
            if (!CanCreateLibraries(httpContext)) {
                return LibraryManagementForbidden();
            }

            var placement = await RestrictedPlacementAsync(
                httpContext, settings, currentUser, request.Path, excludeRootId: null, cancellationToken);
            if (placement is { Allowed: false }) {
                return LibraryPlacementForbidden();
            }

            if (placement is { RequiresNsfw: true }) {
                request = request with { IsNsfw = true };
            }

            LibraryRoot created;
            try {
                created = await settings.CreateLibraryRootAsync(request, cancellationToken, user.Id);
            } catch (ReadOnlyLibraryException exception) {
                return Results.Conflict(new ApiProblem(ApiProblemCodes.ReadOnlyLibrary, exception.Message));
            } catch (LibraryRootPathConflictException exception) {
                return LibraryRootPathConflict(exception);
            }

            // Admins see every library implicitly; explicit grants only exist for members.
            // A member-created root is always creator-only; admins may grant an initial set.
            var grants = user.Role == UserRole.Admin
                ? request.GrantUserIds ?? []
                : [user.Id];
            if (grants.Count > 0) {
                await access.GrantRootAccessAsync(created.Id, grants.ToArray(), cancellationToken);
            }

            return Results.Ok(created with { AccessUserIds = grants });
        })
            .WithName("CreateLibraryRoot")
            .WithSummary("Adds a watched media root.")
            .Produces<LibraryRoot>()
            .Produces<ApiProblem>(StatusCodes.Status403Forbidden)
            .Produces<ApiProblem>(StatusCodes.Status409Conflict);

        group.MapPatch("/{id:guid}", async (
            Guid id,
            LibraryRootUpdateRequest request,
            HttpContext httpContext,
            SettingsService settings,
            ICurrentUserContext currentUser,
            CancellationToken cancellationToken) => {
                if (!await CanManageRootAsync(httpContext, settings, id, cancellationToken)) {
                    return LibraryManagementForbidden();
                }

                if (httpContext.GetCurrentUser() is { Role: not UserRole.Admin } &&
                    await settings.GetLibraryRootAsync(id, cancellationToken) is { } current) {
                    var effectivePath = string.IsNullOrWhiteSpace(request.Path) ? current.Path : request.Path;
                    var placement = await RestrictedPlacementAsync(
                        httpContext, settings, currentUser, effectivePath, id, cancellationToken);
                    if (placement is { Allowed: false }) {
                        return LibraryPlacementForbidden();
                    }

                    if (placement is { RequiresNsfw: true }) {
                        request = request with { IsNsfw = true };
                    }
                }

                LibraryRoot? root;
                try {
                    root = await settings.UpdateLibraryRootAsync(id, request, cancellationToken);
                } catch (ReadOnlyLibraryException exception) {
                    return Results.Conflict(new ApiProblem(ApiProblemCodes.ReadOnlyLibrary, exception.Message));
                } catch (LibraryRootPathConflictException exception) {
                    return LibraryRootPathConflict(exception);
                }

                return root is null ? Results.NotFound() : Results.Ok(root);
            })
            .WithName("UpdateLibraryRoot")
            .WithSummary("Updates a watched media root.")
            .Produces<LibraryRoot>()
            .Produces<ApiProblem>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiProblem>(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            SettingsService settings,
            CancellationToken cancellationToken) => {
                if (!await CanManageRootAsync(httpContext, settings, id, cancellationToken)) {
                    return LibraryManagementForbidden();
                }

                try {
                    var deleted = await settings.DeleteLibraryRootAsync(id, cancellationToken);
                    return deleted ? Results.Ok(new { ok = true }) : Results.NotFound();
                } catch (ReadOnlyLibraryException exception) {
                    return Results.Conflict(new ApiProblem(ApiProblemCodes.ReadOnlyLibrary, exception.Message));
                }
            })
            .WithName("DeleteLibraryRoot")
            .WithSummary("Deletes a watched media root.")
            .Produces<ApiProblem>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}/access", async (
            Guid id,
            LibraryAccessUpdateRequest request,
            SettingsService settings,
            ILibraryAccessStore access,
            CancellationToken cancellationToken) => {
                if (await settings.GetLibraryRootAsync(id, cancellationToken) is null) {
                    return Results.NotFound(new ApiProblem(ApiProblemCodes.RootNotFound, $"Library root '{id}' was not found."));
                }

                await access.ReplaceRootAccessAsync(id, request.UserIds, cancellationToken);
                return Results.NoContent();
            })
            .RequireAdmin()
            .WithName("ReplaceLibraryAccess")
            .WithSummary("Replaces the member users granted access to a library root (admin).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblem>(StatusCodes.Status404NotFound);

        return group;
    }

    private static bool CanCreateLibraries(HttpContext httpContext) =>
        httpContext.GetCurrentUser() is { } user &&
        (user.Role == UserRole.Admin || user.CanCreateLibraries);

    /// <summary>Admins manage every root; members with the permission manage roots they created.</summary>
    private static async Task<bool> CanManageRootAsync(
        HttpContext httpContext,
        SettingsService settings,
        Guid rootId,
        CancellationToken cancellationToken) {
        var user = httpContext.GetCurrentUser();
        if (user is null) {
            return false;
        }

        if (user.Role == UserRole.Admin) {
            return true;
        }

        if (!user.CanCreateLibraries) {
            return false;
        }

        var root = await settings.GetLibraryRootAsync(rootId, cancellationToken);
        return root?.CreatedByUserId == user.Id;
    }

    /// <summary>
    /// Paths of roots a non-admin caller cannot access; empty (no filtering) for admins.
    /// </summary>
    private static async Task<IReadOnlyList<string>> InaccessibleRootPathsAsync(
        HttpContext httpContext,
        SettingsService settings,
        ICurrentUserContext currentUser,
        CancellationToken cancellationToken) {
        var user = httpContext.GetCurrentUser();
        if (user is null || user.Role == UserRole.Admin) {
            return [];
        }

        var allowed = await currentUser.GetAllowedLibraryRootIdsAsync(cancellationToken);
        return allowed is null
            ? []
            : await settings.ListInaccessibleLibraryRootPathsAsync(user.Id, allowed, user.AllowNsfw, cancellationToken);
    }

    /// <summary>
    /// Placement verdict for a non-admin root path, or null when the caller is unrestricted.
    /// </summary>
    private static async Task<LibraryRootPlacement?> RestrictedPlacementAsync(
        HttpContext httpContext,
        SettingsService settings,
        ICurrentUserContext currentUser,
        string path,
        Guid? excludeRootId,
        CancellationToken cancellationToken) {
        var user = httpContext.GetCurrentUser();
        if (user is null || user.Role == UserRole.Admin) {
            return null;
        }

        var allowed = await currentUser.GetAllowedLibraryRootIdsAsync(cancellationToken);
        return allowed is null
            ? null
            : await settings.EvaluateRestrictedLibraryRootPlacementAsync(
                path, user.Id, allowed, user.AllowNsfw, excludeRootId, cancellationToken);
    }

    private static IResult LibraryPlacementForbidden() =>
        Results.Json(
            new ApiProblem(
                ApiProblemCodes.AdminRequired,
                "This folder overlaps a library you do not have access to."),
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult LibraryManagementForbidden() =>
        Results.Json(
            new ApiProblem(ApiProblemCodes.AdminRequired, "You do not have permission to manage this library."),
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult LibraryRootPathConflict(LibraryRootPathConflictException exception) =>
        Results.Conflict(new ApiProblem(ApiProblemCodes.LibraryRootPathConflict, exception.Message));
}
