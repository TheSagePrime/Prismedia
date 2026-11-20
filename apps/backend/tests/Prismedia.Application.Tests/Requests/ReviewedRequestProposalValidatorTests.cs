using Prismedia.Application.Requests;
using Prismedia.Contracts.Entities;
using Prismedia.Contracts.Plugins;
using Prismedia.Contracts.Requests;
using Prismedia.Domain.Entities;

namespace Prismedia.Application.Tests.Requests;

public sealed class ReviewedRequestProposalValidatorTests {
    private static readonly ExternalIdentity Identity = new(ExternalIdProviders.Tmdb, "603");

    [Theory]
    [InlineData("/var/lib/secret.jpg")]
    [InlineData("file:///var/lib/secret.jpg")]
    [InlineData("relative.jpg")]
    public void SubmittedReviewArtworkMustBeRemoteHttpUrls(string reference) {
        var proposal = Proposal("root", [Image(reference)], []);
        var review = Review(proposal);

        Assert.Throws<RequestCommitValidationException>(() =>
            ReviewedRequestProposalValidator.Validate(Request(review, proposal), review, proposal));
    }

    [Fact]
    public void NestedChildArtworkIsValidatedToo() {
        var child = Proposal("child", [Image("/var/lib/secret.jpg")], []);
        var proposal = Proposal("root", [Image("https://images.test/poster.jpg")], [child]);
        var review = Review(proposal);

        Assert.Throws<RequestCommitValidationException>(() =>
            ReviewedRequestProposalValidator.Validate(Request(review, proposal), review, proposal));
    }

    [Fact]
    public void RemoteArtworkIsAccepted() {
        var proposal = Proposal("root", [Image("https://images.test/poster.jpg")], []);
        var review = Review(proposal);
        var request = Request(review, proposal) with {
            SelectedImages = new Dictionary<string, string?> {
                [MediaImageKind.Poster.ToCode()] = "https://images.test/poster.jpg"
            }
        };

        var validated = ReviewedRequestProposalValidator.Validate(request, review, proposal);

        Assert.Same(proposal, validated.Proposal);
    }

    private static ImageCandidate Image(string url) =>
        new(MediaImageKind.Poster.ToCode(), url, "tmdb", 1, null, null, null);

    private static EntityMetadataProposal Proposal(
        string id,
        IReadOnlyList<ImageCandidate> images,
        IReadOnlyList<EntityMetadataProposal> children) =>
        new(
            id,
            "tmdb",
            EntityKind.Movie,
            1,
            null,
            new EntityMetadataPatch(
                "The Matrix",
                null,
                new Dictionary<string, string>(),
                [],
                [],
                null,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                null),
            images,
            children,
            [],
            Relationships: []);

    private static RequestReviewResponse Review(EntityMetadataProposal proposal) =>
        new("movie-plugin", Identity, EntityKind.Movie, RequestMediaKind.Movie, proposal, "revision", []);

    private static ReviewedRequestCommitRequest Request(RequestReviewResponse review, EntityMetadataProposal proposal) =>
        new(
            RequestMediaKind.Movie,
            "movie-plugin",
            Identity,
            "revision",
            [proposal.ProposalId],
            Review: review,
            Proposal: proposal,
            SelectedFields: [MetadataPatchField.Title.ToCode()]);
}
