/**
 * Which optional product areas this install runs, and which routes and entity kinds belong to
 * them. The server decides the profile (`GET /api/features`); this module only maps routes and
 * kinds onto those areas so navigation, the home page, search, and links can hide what is off.
 */
import { ENTITY_KIND, PRODUCT_FEATURE, type ProductFeatureCode } from "$lib/api/generated/codes";

/** Route prefixes owned by each optional area. A prefix matches itself and anything below it. */
const ROUTE_FEATURES: readonly { prefix: string; feature: ProductFeatureCode }[] = [
  { prefix: "/movies", feature: PRODUCT_FEATURE.moviesAndSeries },
  { prefix: "/series", feature: PRODUCT_FEATURE.moviesAndSeries },
  { prefix: "/audio", feature: PRODUCT_FEATURE.audio },
  { prefix: "/artists", feature: PRODUCT_FEATURE.audio },
  { prefix: "/tracks", feature: PRODUCT_FEATURE.audio },
  { prefix: "/books", feature: PRODUCT_FEATURE.books },
  { prefix: "/ebooks", feature: PRODUCT_FEATURE.books },
  { prefix: "/comics", feature: PRODUCT_FEATURE.books },
  { prefix: "/authors", feature: PRODUCT_FEATURE.books },
  { prefix: "/studios", feature: PRODUCT_FEATURE.studios },
  { prefix: "/calendar", feature: PRODUCT_FEATURE.calendar },
  { prefix: "/request", feature: PRODUCT_FEATURE.acquisition },
  { prefix: "/settings/acquisition", feature: PRODUCT_FEATURE.acquisition },
  { prefix: "/settings/connections", feature: PRODUCT_FEATURE.acquisition },
  { prefix: "/settings/auto-identify", feature: PRODUCT_FEATURE.identify },
  { prefix: "/downloads", feature: PRODUCT_FEATURE.acquisition },
  { prefix: "/plugins", feature: PRODUCT_FEATURE.plugins },
  { prefix: "/scrapers", feature: PRODUCT_FEATURE.plugins },
  { prefix: "/identify", feature: PRODUCT_FEATURE.identify },
  { prefix: "/stats", feature: PRODUCT_FEATURE.stats },
  { prefix: "/dev", feature: PRODUCT_FEATURE.developerTools },
  { prefix: "/design-language", feature: PRODUCT_FEATURE.developerTools },
];

/**
 * Entity kinds that exist only inside an optional area. Movies and episodes are deliberately not
 * listed: they are ordinary video files on disk, so with movies and series off they appear and play
 * as plain videos instead of disappearing. Only the series and season containers are hidden.
 */
const KIND_FEATURES: Readonly<Record<string, ProductFeatureCode>> = {
  [ENTITY_KIND.videoSeries]: PRODUCT_FEATURE.moviesAndSeries,
  [ENTITY_KIND.videoSeason]: PRODUCT_FEATURE.moviesAndSeries,
  [ENTITY_KIND.audio]: PRODUCT_FEATURE.audio,
  [ENTITY_KIND.audioLibrary]: PRODUCT_FEATURE.audio,
  [ENTITY_KIND.audioTrack]: PRODUCT_FEATURE.audio,
  [ENTITY_KIND.musicArtist]: PRODUCT_FEATURE.audio,
  [ENTITY_KIND.book]: PRODUCT_FEATURE.books,
  [ENTITY_KIND.bookVolume]: PRODUCT_FEATURE.books,
  [ENTITY_KIND.bookChapter]: PRODUCT_FEATURE.books,
  [ENTITY_KIND.bookAuthor]: PRODUCT_FEATURE.books,
  [ENTITY_KIND.comicInstallment]: PRODUCT_FEATURE.books,
  [ENTITY_KIND.comicSeries]: PRODUCT_FEATURE.books,
  [ENTITY_KIND.comicVolume]: PRODUCT_FEATURE.books,
  [ENTITY_KIND.studio]: PRODUCT_FEATURE.studios,
};

/** Kinds that play as videos when movies and series are switched off. */
const PLAIN_VIDEO_KINDS: readonly string[] = [ENTITY_KIND.movie, ENTITY_KIND.videoEpisode];

/**
 * Enabled areas, or `null` when unknown (not loaded yet, or the server could not be reached).
 * Unknown means "everything on", so a failed lookup never hides the user's own pages.
 */
export type EnabledFeatures = ReadonlySet<ProductFeatureCode> | null;

function isOn(feature: ProductFeatureCode | undefined, enabled: EnabledFeatures): boolean {
  return feature === undefined || enabled === null || enabled.has(feature);
}

/** The optional area that owns a path, or undefined for core pages. */
export function featureForPath(pathname: string): ProductFeatureCode | undefined {
  return ROUTE_FEATURES.find(({ prefix }) => pathname === prefix || pathname.startsWith(`${prefix}/`))?.feature;
}

/** Whether a page is available under the enabled areas. */
export function isPathEnabled(pathname: string, enabled: EnabledFeatures): boolean {
  return isOn(featureForPath(pathname), enabled);
}

/** Whether items of an entity kind are shown under the enabled areas. */
export function isKindEnabled(kind: string, enabled: EnabledFeatures): boolean {
  return isOn(KIND_FEATURES[kind], enabled);
}

/** Whether movies and episodes should be presented as plain videos. */
export function showsMoviesAsVideos(enabled: EnabledFeatures): boolean {
  return !isOn(PRODUCT_FEATURE.moviesAndSeries, enabled);
}

/** Whether a kind is a movie or episode that plays as a plain video under the enabled areas. */
export function isPlainVideoKind(kind: string, enabled: EnabledFeatures): boolean {
  return showsMoviesAsVideos(enabled) && PLAIN_VIDEO_KINDS.includes(kind);
}

/** Kinds the Videos page lists: plain videos, plus movies and episodes when those pages are off. */
export function videoListKinds(enabled: EnabledFeatures): string[] {
  return showsMoviesAsVideos(enabled) ? [ENTITY_KIND.video, ...PLAIN_VIDEO_KINDS] : [ENTITY_KIND.video];
}
