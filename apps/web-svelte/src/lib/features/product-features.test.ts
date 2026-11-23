import { describe, expect, it } from "vitest";
import { ENTITY_KIND, PRODUCT_FEATURE, type ProductFeatureCode } from "$lib/api/generated/codes";
import { buildAvailableNavCatalog } from "$lib/nav/nav-catalog";
import {
  featureForPath,
  isKindEnabled,
  isPathEnabled,
  isPlainVideoKind,
  videoListKinds,
} from "./product-features";

const simple = new Set<ProductFeatureCode>();
const full = new Set<ProductFeatureCode>(Object.values(PRODUCT_FEATURE));

describe("product features", () => {
  it("maps hidden pages, including their detail and settings pages, to their area", () => {
    expect(featureForPath("/movies")).toBe(PRODUCT_FEATURE.moviesAndSeries);
    expect(featureForPath("/series/123/seasons/4")).toBe(PRODUCT_FEATURE.moviesAndSeries);
    expect(featureForPath("/settings/connections/abc")).toBe(PRODUCT_FEATURE.acquisition);
    expect(featureForPath("/design-language")).toBe(PRODUCT_FEATURE.developerTools);
    expect(featureForPath("/moviesque")).toBeUndefined();
  });

  it("keeps folders, photos, videos, people, tags, collections, search, settings and jobs", () => {
    for (const path of ["/", "/files", "/images", "/videos/1", "/galleries", "/people", "/tags", "/collections", "/search", "/settings", "/settings/libraries", "/jobs", "/account"]) {
      expect(isPathEnabled(path, simple)).toBe(true);
    }
    for (const path of ["/movies", "/audio", "/books", "/studios", "/calendar", "/downloads", "/request", "/plugins", "/scrapers", "/identify", "/stats", "/dev"]) {
      expect(isPathEnabled(path, simple)).toBe(false);
      expect(isPathEnabled(path, full)).toBe(true);
    }
  });

  it("shows everything when the enabled set is unknown", () => {
    expect(isPathEnabled("/movies", null)).toBe(true);
    expect(isKindEnabled(ENTITY_KIND.studio, null)).toBe(true);
  });

  it("presents movies and episodes as plain videos instead of hiding them", () => {
    expect(isKindEnabled(ENTITY_KIND.movie, simple)).toBe(true);
    expect(isKindEnabled(ENTITY_KIND.videoEpisode, simple)).toBe(true);
    expect(isKindEnabled(ENTITY_KIND.videoSeries, simple)).toBe(false);
    expect(isKindEnabled(ENTITY_KIND.book, simple)).toBe(false);
    expect(isPlainVideoKind(ENTITY_KIND.movie, simple)).toBe(true);
    expect(isPlainVideoKind(ENTITY_KIND.movie, full)).toBe(false);
    expect(videoListKinds(simple)).toEqual([ENTITY_KIND.video, ENTITY_KIND.movie, ENTITY_KIND.videoEpisode]);
    expect(videoListKinds(full)).toEqual([ENTITY_KIND.video]);
  });

  it("drops hidden routes from navigation and labels Files as Folders in the simple library", () => {
    const simpleNav = buildAvailableNavCatalog(simple, true);
    const hrefs = simpleNav.map((item) => item.href as string);

    expect(hrefs).toEqual(expect.arrayContaining(["/", "/search", "/files", "/videos", "/images", "/galleries", "/people", "/tags", "/collections", "/jobs", "/settings"]));
    expect(hrefs).not.toEqual(expect.arrayContaining(["/movies"]));
    for (const hidden of ["/movies", "/series", "/audio", "/books", "/studios", "/calendar", "/downloads", "/request", "/plugins", "/identify", "/stats"]) {
      expect(hrefs).not.toContain(hidden);
    }
    expect(simpleNav.find((item) => item.href === "/files")?.label).toBe("Folders");
    expect(buildAvailableNavCatalog(full, false).find((item) => item.href === "/files")?.label).toBe("Files");
  });
});
