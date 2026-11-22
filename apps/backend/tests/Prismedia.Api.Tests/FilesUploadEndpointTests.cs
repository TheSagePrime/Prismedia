using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Prismedia.Application.Files;

namespace Prismedia.Api.Tests;

public sealed class FilesUploadEndpointTests {
    [Fact]
    public void ChunkEndpointAcceptsOneFullChunkPerRequest() {
        using var factory = new WebApplicationFactory<Program>();
        _ = factory.Server;
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var chunk = endpoints.Single(candidate =>
            candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "UploadFileChunk");

        Assert.Equal(FileUploadStaging.MaxChunkBytes, chunk.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    [Fact]
    public void ResumableUploadEndpointsReplaceTheSingleRequestUpload() {
        using var factory = new WebApplicationFactory<Program>();
        _ = factory.Server;
        var names = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .ToHashSet();

        Assert.Subset(names, new HashSet<string?> {
            "CreateFileUpload", "GetFileUpload", "UploadFileChunk", "CompleteFileUpload", "CancelFileUpload",
        });
        Assert.DoesNotContain("UploadFiles", names);
    }
}
