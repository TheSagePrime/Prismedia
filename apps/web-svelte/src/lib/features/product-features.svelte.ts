import { FEATURE_PROFILE, type FeatureProfileCode, type ProductFeatureCode } from "$lib/api/generated/codes";
import type { EnabledFeatures } from "./product-features";

/**
 * App-wide feature profile and enabled areas, set once at boot from `GET /api/features`. Reactive,
 * so navigation and pages update when the set arrives. `null` means unknown and shows everything.
 */
class ProductFeatureStore {
  enabled = $state.raw<EnabledFeatures>(null);
  profile = $state.raw<FeatureProfileCode | null>(null);

  /** Whether this install runs the simple photo and video library profile. */
  get simpleLibrary(): boolean {
    return this.profile === FEATURE_PROFILE.simple;
  }

  /** Records the server's profile and enabled areas; null when they could not be loaded. */
  set(snapshot: { profile: FeatureProfileCode; enabled: readonly ProductFeatureCode[] } | null): void {
    this.profile = snapshot?.profile ?? null;
    this.enabled = snapshot === null ? null : new Set(snapshot.enabled);
  }
}

export const productFeatures = new ProductFeatureStore();
