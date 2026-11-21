using Microsoft.AspNetCore.Mvc;
using Prismedia.Application.Files;
using Prismedia.Contracts.Files;

namespace Prismedia.Api.Endpoints;

/// <summary>
/// Resumable Files uploads: start an upload, send ordered chunks, check progress after an
/// interruption, then complete or cancel it. Each request carries at most one chunk, so request
/// bodies stay small and nothing is buffered outside the library filesystem.
/// </summary>
internal static class FilesUploadEndpoint {
    internal static RouteGroupBuilder MapFilesUploadEndpoint(this RouteGroupBuilder group) {
        group.MapPost("/uploads", async (
            FileUploadCreateRequest request,
            bool? hideNsfw,
            HttpContext httpContext,
            FilesService files,
            CancellationToken cancellationToken) =>
            FilesEndpoints.ToResult(await FilesEndpoints.RunAsync(() => files.CreateUploadAsync(
                request,
                NsfwVisibility.ShouldHide(hideNsfw, httpContext),
                cancellationToken))))
            .WithName("CreateFileUpload")
            .WithSummary("Starts a resumable upload of one file into a watched-root folder.")
            .Produces<FileUploadCreateResponse>();

        group.MapGet("/uploads/{id:guid}", async (
            Guid id,
            FilesService files,
            CancellationToken cancellationToken) =>
            FilesEndpoints.ToResult(await FilesEndpoints.RunAsync(() => files.GetUploadAsync(id, cancellationToken))))
            .WithName("GetFileUpload")
            .WithSummary("Gets how many bytes of a resumable upload have been stored.")
            .Produces<FileUploadSession>();

        group.MapPut("/uploads/{id:guid}/chunks", async (
            Guid id,
            long offset,
            HttpRequest request,
            FilesService files,
            CancellationToken cancellationToken) =>
            FilesEndpoints.ToResult(await FilesEndpoints.RunAsync(() => files.AppendUploadChunkAsync(
                id,
                offset,
                request.ContentLength,
                request.Body,
                cancellationToken))))
            .WithName("UploadFileChunk")
            .WithSummary("Stores one chunk of a resumable upload starting at the given byte offset.")
            .Accepts<Stream>("application/octet-stream")
            .WithMetadata(new RequestSizeLimitAttribute(FileUploadStaging.MaxChunkBytes))
            .DisableAntiforgery()
            .Produces<FileUploadSession>();

        group.MapPost("/uploads/{id:guid}/complete", async (
            Guid id,
            bool? hideNsfw,
            HttpContext httpContext,
            FilesService files,
            CancellationToken cancellationToken) =>
            FilesEndpoints.ToResult(await FilesEndpoints.RunAsync(() => files.CompleteUploadAsync(
                id,
                NsfwVisibility.ShouldHide(hideNsfw, httpContext),
                cancellationToken))))
            .WithName("CompleteFileUpload")
            .WithSummary("Moves a fully received upload into place, applying its conflict choice.")
            .Produces<FileUploadCompletion>();

        group.MapDelete("/uploads/{id:guid}", async (
            Guid id,
            FilesService files,
            CancellationToken cancellationToken) => {
            var result = await FilesEndpoints.RunAsync(async () => {
                await files.CancelUploadAsync(id, cancellationToken);
                return true;
            });
            return result.Error is null ? Results.NoContent() : FilesEndpoints.ToResult(result);
        })
            .WithName("CancelFileUpload")
            .WithSummary("Cancels a resumable upload and deletes its partial bytes.")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}
