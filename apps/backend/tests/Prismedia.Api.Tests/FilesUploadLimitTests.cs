using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Prismedia.Api.Tests;

public sealed class FilesUploadLimitTests {
    [Fact]
    public void FilesUploadAcceptsLargeVideoSizedRequests() {
        using var factory = new WebApplicationFactory<Program>();
        _ = factory.Server;
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Single(candidate =>
                candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "UploadFiles");

        var bodyLimit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        var formLimit = endpoint.Metadata.GetMetadata<IFormOptionsMetadata>();

        Assert.Equal(Endpoints.FilesUploadEndpoint.FilesUploadLimitBytes, bodyLimit?.MaxRequestBodySize);
        Assert.Equal(Endpoints.FilesUploadEndpoint.FilesUploadLimitBytes, formLimit?.MultipartBodyLengthLimit);
        Assert.True(Endpoints.FilesUploadEndpoint.FilesUploadLimitBytes > 10L * 1024 * 1024 * 1024);
    }
}
