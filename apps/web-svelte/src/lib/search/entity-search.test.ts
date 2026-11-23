import { describe, expect, it } from "vitest";
import type { EntityKind } from "$lib/api/generated/model";
import type { EntityCard, EntityListResponse } from "$lib/api/entities";
import { afterEach } from "vitest";
import { availableSearchKinds, firstSearchResult, flattenSearchResults, searchEntities } from "./entity-search";
import { productFeatures } from "$lib/features/product-features.svelte";

const emptyPage: EntityListResponse = { items: [], nextCursor: null, totalCount: 0 };

describe("searchEntities in the simple library", () => {
  afterEach(() => productFeatures.set(null));

  it("drops hidden kinds and lists movie and episode files as videos", async () => {
    productFeatures.set({ profile: "simple", enabled: [] });
    const response = await searchEntities({
      query: "beach",
      includeRelated: false,
      fetcher: async () => page([
        entity("movie-1", "movie", "Beach day"),
        entity("book-1", "book", "Beach reads"),
        entity("image-1", "image", "Beach sunset"),
      ]),
    });

    const items = flattenSearchResults(response);
    expect(items.map((item) => [item.title, item.kind, item.href])).toEqual([
      ["Beach day", "video", "/videos/movie-1"],
      ["Beach sunset", "image", "/images/image-1"],
    ]);
    expect(availableSearchKinds()).not.toContain("movie");
    expect(availableSearchKinds()).not.toContain("book");
    expect(availableSearchKinds()).toContain("video");
  });
});

describe("searchEntities", () => {
  it("returns direct matches before incoming relationship matches", async () => {
    const tim = entity("person-1", "person", "Tim Robinson");
    const sketch = entity("video-1", "video", "Hot Dog Car");
    const series = entity("series-1", "video-series", "I Think You Should Leave");
    const calls: unknown[] = [];

    const response = await searchEntities({
      query: "Tim Robinson",
      fetcher: async (params) => {
        calls.push(params);
        if (params?.query === "Tim Robinson") return page([tim]);
        if (params?.referencedBy === "person-1") return page([sketch, series]);
        return emptyPage;
      },
    });

    expect(flattenSearchResults(response).map((item) => item.title)).toEqual([
      "Tim Robinson",
      "Hot Dog Car",
      "I Think You Should Leave",
    ]);
    expect(flattenSearchResults(response).map((item) => item.matchType)).toEqual([
      "direct",
      "related",
      "related",
    ]);
    expect(firstSearchResult(response)?.href).toBe("/people/person-1");
    expect(firstSearchResult(response)?.thumbnail).toEqual(tim);
    expect(calls).toContainEqual(expect.objectContaining({ referencedBy: "person-1" }));
  });

  it("filters output kinds without hiding related matches from unselected source kinds", async () => {
    const tim = entity("person-1", "person", "Tim Robinson");
    const sketch = entity("video-1", "video", "Hot Dog Car");

    const response = await searchEntities({
      query: "Tim",
      kinds: ["video"],
      fetcher: async (params) => {
        if (params?.query === "Tim") return page([tim]);
        if (params?.referencedBy === "person-1") return page([sketch]);
        return emptyPage;
      },
    });

    expect(response.groups).toHaveLength(1);
    expect(response.groups[0].kind).toBe("video");
    expect(response.groups[0].items[0]).toMatchObject({
      title: "Hot Dog Car",
      relatedTo: { title: "Tim Robinson" },
    });
  });

  it("deduplicates entities that are also direct matches", async () => {
    const tim = entity("person-1", "person", "Tim Robinson");
    const sketch = entity("video-1", "video", "Tim's Sketch");

    const response = await searchEntities({
      query: "Tim",
      fetcher: async (params) => {
        if (params?.query === "Tim") return page([tim, sketch]);
        if (params?.referencedBy === "person-1") return page([sketch]);
        return emptyPage;
      },
    });

    expect(flattenSearchResults(response).map((item) => item.id)).toEqual(["person-1", "video-1"]);
  });
});

function entity(id: string, kind: EntityKind, title: string): EntityCard {
  return {
    id,
    kind,
    title,
    parentEntityId: null,
    sortOrder: null,
    coverUrl: null,
    coverThumbUrl: null,
    hoverKind: "none",
    hoverUrl: null,
    hoverImages: [],
    meta: [],
    rating: null,
    isFavorite: false,
    isNsfw: false,
    isOrganized: true,
  };
}

function page(items: EntityCard[]): EntityListResponse {
  return { items, nextCursor: null, totalCount: items.length };
}
