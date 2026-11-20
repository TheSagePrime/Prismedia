using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Prismedia.Application.Settings;
using Prismedia.Contracts.Settings;
using Prismedia.Contracts.System;
using Prismedia.Domain.Entities;

namespace Prismedia.Api.Tests;

public sealed class SettingsEndpointServiceTests {
    [Fact]
    public async Task SettingsCatalogEndpointReadsAndUpdatesThroughService() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var catalog = await client.GetFromJsonAsync<SettingsCatalogResponse>("/api/settings");
        var updatedResponse = await client.PatchAsJsonAsync(
            $"/api/settings/{AppSettings.Jobs.BackgroundConcurrency.Key}",
            new SettingUpdateRequest(JsonSerializer.SerializeToElement(6)));
        var updated = await updatedResponse.Content.ReadFromJsonAsync<SettingDescriptor>();
        var values = await client.GetFromJsonAsync<SettingsValuesResponse>(
            $"/api/settings/values?keys={Uri.EscapeDataString(AppSettings.Jobs.BackgroundConcurrency.Key)}");

        Assert.NotNull(catalog);
        Assert.Contains(catalog.Groups.SelectMany(group => group.Settings), setting =>
            setting.Key == AppSettings.Visibility.DefaultMode.Key &&
            setting.Value.GetString() == "off" &&
            setting.IsDefault);
        Assert.True(updatedResponse.IsSuccessStatusCode);
        Assert.NotNull(updated);
        Assert.Equal(6, updated.Value.GetInt32());
        Assert.False(updated.IsDefault);
        Assert.NotNull(values);
        Assert.Equal(6, values.Values[AppSettings.Jobs.BackgroundConcurrency.Key].GetInt32());
    }

    [Fact]
    public async Task SettingsBatchAndResetEndpointsUseCentralRegistry() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var batch = await client.PatchAsJsonAsync(
            "/api/settings",
            new SettingsBatchUpdateRequest(new Dictionary<string, JsonElement> {
                [AppSettings.Playback.DefaultMode.Key] = JsonSerializer.SerializeToElement("hls"),
                [AppSettings.Playback.AudioPreferredLanguages.Key] =
                    JsonSerializer.SerializeToElement(new[] { "ja", "jpn" }),
            }));
        var reset = await client.DeleteAsync($"/api/settings/{AppSettings.Playback.DefaultMode.Key}");
        var defaulted = await reset.Content.ReadFromJsonAsync<SettingDescriptor>();

        Assert.True(batch.IsSuccessStatusCode);
        Assert.True(reset.IsSuccessStatusCode);
        Assert.NotNull(defaulted);
        Assert.Equal("direct", defaulted.Value.GetString());
        Assert.True(defaulted.IsDefault);
    }

    [Fact]
    public async Task SettingsValuesExposeTypedPerKindIdentifyProviderDefaults() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();
        var configured = new Dictionary<string, string> {
            [EntityKind.Movie.ToCode()] = "tmdb",
            [EntityKind.Book.ToCode()] = "openlibrary",
        };

        var update = await client.PatchAsJsonAsync(
            $"/api/settings/{AppSettings.Identify.DefaultProviders.Key}",
            new SettingUpdateRequest(JsonSerializer.SerializeToElement(configured)));
        var descriptor = await update.Content.ReadFromJsonAsync<SettingDescriptor>();
        var values = await client.GetFromJsonAsync<SettingsValuesResponse>(
            $"/api/settings/values?keys={Uri.EscapeDataString(AppSettings.Identify.DefaultProviders.Key)}");

        Assert.True(update.IsSuccessStatusCode);
        Assert.NotNull(descriptor);
        Assert.Equal("stringMap", descriptor.Type);
        Assert.Equal(configured, descriptor.Value.Deserialize<Dictionary<string, string>>());
        Assert.Empty(descriptor.DefaultValue.EnumerateObject());
        Assert.NotNull(values);
        var defaults = values.Values[AppSettings.Identify.DefaultProviders.Key]
            .Deserialize<Dictionary<string, string>>();
        Assert.Equal(configured, defaults);
    }

    [Fact]
    public async Task SettingsEndpointsReturnProblemDetailsForUnknownAndInvalidKeys() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var unknown = await client.GetAsync("/api/settings/not.real");
        var invalid = await client.PatchAsJsonAsync(
            $"/api/settings/{AppSettings.Jobs.BackgroundConcurrency.Key}",
            new SettingUpdateRequest(JsonSerializer.SerializeToElement(99)));
        var unknownJson = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        var invalidJson = await invalid.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("setting_not_found", unknownJson.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("setting_invalid", invalidJson.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LibraryConfigPayloadIncludesCatalogAndWatchedRoots() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var config = await client.GetFromJsonAsync<LibraryConfigResponse>("/api/settings/library");
        var roots = await client.GetFromJsonAsync<IReadOnlyList<LibraryRoot>>("/api/libraries");
        var root = await client.PostAsJsonAsync(
            "/api/libraries",
            new LibraryRootCreateRequest("/media/series", "Series", false, null, null, null, null, null, null));

        Assert.NotNull(config);
        Assert.Single(config.Roots);
        Assert.NotEmpty(config.Settings.Groups);
        Assert.NotNull(roots);
        Assert.Single(roots);
        Assert.True(root.IsSuccessStatusCode);
    }

    [Fact]
    public async Task CreatingAnExistingLibraryRootPathReturnsTypedConflict() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/api/libraries",
            new LibraryRootCreateRequest("/media/videos", "Duplicate", false, null, null, null, null, null, null));
        var problem = await response.Content.ReadFromJsonAsync<ApiProblem>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(problem);
        Assert.Equal(ApiProblemCodes.LibraryRootPathConflict, problem.Code);
        Assert.Contains("/media/videos", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingToAnExistingLibraryRootPathReturnsTypedConflict() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/libraries",
            new LibraryRootCreateRequest("/media/series", "Series", false, null, null, null, null, null, null));
        var created = await createResponse.Content.ReadFromJsonAsync<LibraryRoot>();
        Assert.NotNull(created);

        using var response = await client.PatchAsJsonAsync(
            $"/api/libraries/{created.Id}",
            new LibraryRootUpdateRequest(
                Path: "/media/videos",
                Label: null,
                Enabled: null,
                Recursive: null,
                ScanVideos: null,
                ScanImages: null,
                ScanAudio: null,
                ScanBooks: null,
                IsNsfw: null));
        var problem = await response.Content.ReadFromJsonAsync<ApiProblem>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(problem);
        Assert.Equal(ApiProblemCodes.LibraryRootPathConflict, problem.Code);
        Assert.Contains("/media/videos", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MembersReadAccessibleLibrarySummariesButNotFullRoots() {
        using var factory = CreateFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var createUser = await admin.PostAsJsonAsync(
            "/api/users",
            new { Username = "reader", Password = "reader-password" });
        createUser.EnsureSuccessStatusCode();

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = "reader", Password = "reader-password" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();

        using var accessibleRequest = new HttpRequestMessage(HttpMethod.Get, "/api/libraries/accessible");
        accessibleRequest.Headers.Authorization = new("Bearer", token);
        using var accessible = await client.SendAsync(accessibleRequest);
        using var fullRequest = new HttpRequestMessage(HttpMethod.Get, "/api/libraries");
        fullRequest.Headers.Authorization = new("Bearer", token);
        using var full = await client.SendAsync(fullRequest);

        Assert.Equal(HttpStatusCode.OK, accessible.StatusCode);
        // No grants yet: the member sees an empty summary list, never host paths.
        Assert.Empty((await accessible.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, full.StatusCode);
    }

    [Fact]
    public async Task AccessibleLibrarySummariesHideNsfwRootsForASfwViewer() {
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var create = await client.PostAsJsonAsync(
            "/api/libraries",
            new LibraryRootCreateRequest(
                "/media/hidden",
                "Hidden",
                Enabled: false,
                Recursive: null,
                ScanVideos: null,
                ScanImages: null,
                ScanAudio: null,
                ScanBooks: true,
                IsNsfw: true));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<LibraryRoot>();
        Assert.NotNull(created);
        using var enable = await client.PatchAsJsonAsync(
            $"/api/libraries/{created.Id}",
            new LibraryRootUpdateRequest(
                Path: null,
                Label: null,
                Enabled: true,
                Recursive: null,
                ScanVideos: null,
                ScanImages: null,
                ScanAudio: null,
                ScanBooks: null,
                IsNsfw: null));
        enable.EnsureSuccessStatusCode();

        var roots = await client.GetFromJsonAsync<IReadOnlyList<LibraryRootSummary>>("/api/libraries/accessible");

        var visible = Assert.Single(roots!);
        Assert.Equal("Videos", visible.Label);
        Assert.False(visible.IsNsfw);
    }

    [Fact]
    public async Task LibraryCreatorsListOnlyTheRootsTheyCreated() {
        using var factory = CreateFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var createUser = await admin.PostAsJsonAsync(
            "/api/users",
            new { Username = "creator", Password = "creator-password", CanCreateLibraries = true });
        createUser.EnsureSuccessStatusCode();

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = "creator", Password = "creator-password" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using var created = await client.PostAsJsonAsync(
            "/api/libraries",
            new LibraryRootCreateRequest("/media/mine", "Mine", false, null, null, null, null, null, null));
        var listed = await client.GetFromJsonAsync<IReadOnlyList<LibraryRoot>>("/api/libraries");

        Assert.True(created.IsSuccessStatusCode);
        Assert.NotNull(listed);
        // The pre-seeded admin root is invisible; only the member's own creation lists.
        var own = Assert.Single(listed);
        Assert.Equal("Mine", own.Label);
        Assert.NotNull(own.CreatedByUserId);
    }

    [Theory]
    [InlineData("/media")]
    [InlineData("/media/videos")]
    [InlineData("/media/videos/")]
    [InlineData("/media/videos/sub")]
    [InlineData("/media/other/../videos/sub")]
    public async Task LibraryCreatorsCannotOverlapRootsTheyCannotAccess(string path) {
        using var factory = CreateFactory();
        using var client = await CreateLibraryCreatorClientAsync(factory, allowNsfw: true);

        using var response = await client.PostAsJsonAsync("/api/libraries", Root(path, isNsfw: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var roots = await factory.Services.GetRequiredService<ISettingsPersistence>()
            .ListLibraryRootsAsync(CancellationToken.None);
        Assert.Single(roots);
    }

    [Fact]
    public async Task LibraryCreatorsMayUseSiblingAndGrantedPaths() {
        using var factory = CreateFactory();
        using var client = await CreateLibraryCreatorClientAsync(factory, allowNsfw: true);

        using var sibling = await client.PostAsJsonAsync("/api/libraries", Root("/media/videos2", isNsfw: false));
        Assert.Equal(HttpStatusCode.OK, sibling.StatusCode);

        using var admin = factory.CreateAuthenticatedClient();
        var userId = await UserIdAsync(admin, "creator");
        using var grant = await admin.PutAsJsonAsync(
            $"/api/libraries/{SampleRootId}/access",
            new LibraryAccessUpdateRequest([userId]));
        grant.EnsureSuccessStatusCode();

        using var nested = await client.PostAsJsonAsync("/api/libraries", Root("/media/videos/sub", isNsfw: false));
        Assert.Equal(HttpStatusCode.OK, nested.StatusCode);
    }

    [Fact]
    public async Task LibraryCreatorRootsInsideNsfwRootsAreForcedNsfw() {
        using var factory = CreateFactory();
        using var client = await CreateLibraryCreatorClientAsync(factory, allowNsfw: true);
        using var admin = factory.CreateAuthenticatedClient();
        var userId = await UserIdAsync(admin, "creator");
        using var adult = await admin.PostAsJsonAsync(
            "/api/libraries",
            Root("/media/adult", isNsfw: true) with { GrantUserIds = [userId] });
        adult.EnsureSuccessStatusCode();
        await EnableDirectlyAsync(factory, adult);

        using var nested = await client.PostAsJsonAsync("/api/libraries", Root("/media/adult/sub", isNsfw: false));
        var created = await nested.Content.ReadFromJsonAsync<LibraryRoot>();

        Assert.Equal(HttpStatusCode.OK, nested.StatusCode);
        Assert.True(created!.IsNsfw);

        using var clear = await client.PatchAsJsonAsync(
            $"/api/libraries/{created.Id}",
            new LibraryRootUpdateRequest(null, null, null, null, null, null, null, null, IsNsfw: false));
        var updated = await clear.Content.ReadFromJsonAsync<LibraryRoot>();
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.True(updated!.IsNsfw);
    }

    [Fact]
    public async Task SfwLibraryCreatorsCannotNestInsideGrantedNsfwRoots() {
        using var factory = CreateFactory();
        using var client = await CreateLibraryCreatorClientAsync(factory, allowNsfw: false);
        using var admin = factory.CreateAuthenticatedClient();
        var userId = await UserIdAsync(admin, "creator");
        using var adult = await admin.PostAsJsonAsync(
            "/api/libraries",
            Root("/media/adult", isNsfw: true) with { GrantUserIds = [userId] });
        adult.EnsureSuccessStatusCode();
        await EnableDirectlyAsync(factory, adult);

        using var nested = await client.PostAsJsonAsync("/api/libraries", Root("/media/adult/sub", isNsfw: false));

        Assert.Equal(HttpStatusCode.Forbidden, nested.StatusCode);
    }

    [Fact]
    public async Task LibraryCreatorsCannotMoveTheirRootIntoAnInaccessibleRoot() {
        using var factory = CreateFactory();
        using var client = await CreateLibraryCreatorClientAsync(factory, allowNsfw: true);
        using var create = await client.PostAsJsonAsync("/api/libraries", Root("/media/mine", isNsfw: false));
        var created = await create.Content.ReadFromJsonAsync<LibraryRoot>();

        using var move = await client.PatchAsJsonAsync(
            $"/api/libraries/{created!.Id}",
            new LibraryRootUpdateRequest("/media/videos/inner", null, null, null, null, null, null, null, null));
        using var rename = await client.PatchAsJsonAsync(
            $"/api/libraries/{created.Id}",
            new LibraryRootUpdateRequest(null, "Renamed", null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    [Fact]
    public async Task LibraryCreatorBrowseHidesRootsTheyCannotAccess() {
        var parent = Directory.CreateTempSubdirectory("prismedia-browse-");
        try {
            var hidden = parent.CreateSubdirectory("hidden");
            hidden.CreateSubdirectory("inside");
            parent.CreateSubdirectory("open");
            using var factory = CreateFactory();
            using var admin = factory.CreateAuthenticatedClient();
            using var adminRoot = await admin.PostAsJsonAsync(
                "/api/libraries",
                Root(hidden.FullName, isNsfw: false) with { Enabled = false });
            adminRoot.EnsureSuccessStatusCode();
            using var client = await CreateLibraryCreatorClientAsync(factory, allowNsfw: true);

            var listing = await client.GetFromJsonAsync<LibraryBrowseResponse>(
                $"/api/libraries/browse?path={Uri.EscapeDataString(parent.FullName)}");
            using var inside = await client.GetAsync(
                $"/api/libraries/browse?path={Uri.EscapeDataString(Path.Combine(hidden.FullName, "inside"))}");
            var adminListing = await admin.GetFromJsonAsync<LibraryBrowseResponse>(
                $"/api/libraries/browse?path={Uri.EscapeDataString(parent.FullName)}");

            Assert.Equal(["open"], listing!.Directories.Select(entry => entry.Name).ToArray());
            Assert.Equal(HttpStatusCode.Forbidden, inside.StatusCode);
            Assert.Equal(2, adminListing!.Directories.Count);
        } finally {
            parent.Delete(recursive: true);
        }
    }

    /// <summary>Enables a root in the fake store without queueing scans through the real job queue.</summary>
    private static async Task EnableDirectlyAsync(WebApplicationFactory<Program> factory, HttpResponseMessage created) {
        var root = await created.Content.ReadFromJsonAsync<LibraryRoot>();
        await factory.Services.GetRequiredService<ISettingsPersistence>()
            .SaveLibraryRootAsync(root! with { Enabled = true }, CancellationToken.None);
    }

    private static readonly Guid SampleRootId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static LibraryRootCreateRequest Root(string path, bool isNsfw) =>
        new(path, null, Enabled: false, null, null, null, null, null, IsNsfw: isNsfw);

    private static async Task<Guid> UserIdAsync(HttpClient admin, string username) {
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        return users.GetProperty("items").EnumerateArray()
            .Single(user => user.GetProperty("username").GetString() == username)
            .GetProperty("id")
            .GetGuid();
    }

    private static async Task<HttpClient> CreateLibraryCreatorClientAsync(
        WebApplicationFactory<Program> factory,
        bool allowNsfw) {
        using var admin = factory.CreateAuthenticatedClient();
        using var createUser = await admin.PostAsJsonAsync(
            "/api/users",
            new { Username = "creator", Password = "creator-password", CanCreateLibraries = true, AllowNsfw = allowNsfw });
        createUser.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = "creator", Password = "creator-password" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => {
                builder.ConfigureServices(services => {
                    services.AddSingleton<ISettingsPersistence, FakeSettingsPersistence>();
                });
            })
            .WithTestAuth();

    private sealed class FakeSettingsPersistence : ISettingsPersistence {
        private static readonly Guid RootId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, LibraryRoot> _roots = new() { [RootId] = SampleRoot() };

        public Task<IReadOnlyDictionary<string, string>> LoadSettingOverridesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_settings, StringComparer.Ordinal));

        public Task SaveSettingOverrideAsync(string key, string valueJson, CancellationToken cancellationToken) {
            _settings[key] = valueJson;
            return Task.CompletedTask;
        }

        public Task SaveSettingOverridesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) {
            foreach (var (key, valueJson) in values) {
                _settings[key] = valueJson;
            }

            return Task.CompletedTask;
        }

        public Task ReplaceSettingOverridesAsync(
            IReadOnlyDictionary<string, string> upserts,
            IReadOnlyCollection<string> deletes,
            CancellationToken cancellationToken) {
            foreach (var key in deletes) {
                _settings.Remove(key);
            }

            foreach (var (key, valueJson) in upserts) {
                _settings[key] = valueJson;
            }

            return Task.CompletedTask;
        }

        public Task DeleteSettingOverrideAsync(string key, CancellationToken cancellationToken) {
            _settings.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LibraryRoot>> ListLibraryRootsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryRoot>>(_roots.Values.ToArray());

        public Task<LibraryRoot?> GetLibraryRootAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<LibraryRoot?>(_roots.GetValueOrDefault(id));

        public Task<LibraryRoot> AddLibraryRootAsync(LibraryRoot state, CancellationToken cancellationToken) {
            ThrowIfPathBelongsToAnotherRoot(state);
            _roots[state.Id] = state;
            return Task.FromResult(state);
        }

        public Task<LibraryRoot> SaveLibraryRootAsync(LibraryRoot state, CancellationToken cancellationToken) {
            ThrowIfPathBelongsToAnotherRoot(state);
            _roots[state.Id] = state;
            return Task.FromResult(state);
        }

        public Task<bool> DeleteLibraryRootAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_roots.Remove(id));

        private void ThrowIfPathBelongsToAnotherRoot(LibraryRoot state) {
            if (_roots.Values.Any(root => root.Id != state.Id && root.Path == state.Path)) {
                throw new LibraryRootPathConflictException(state.Path);
            }
        }

        private static LibraryRoot SampleRoot() =>
            new(
                RootId,
                "/media/videos",
                "Videos",
                true,
                true,
                true,
                false,
                false,
                false,
                false,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }
}
