import { getProductFeatures } from "$lib/api/generated/prismedia";
import type { FeatureProfileCode, ProductFeatureCode } from "$lib/api/generated/codes";
import { unwrapGenerated } from "$lib/api/generated-response";
import type { ProductFeaturesResponse } from "$lib/api/generated/model";

/** Feature profile and enabled optional areas of this install. */
export interface ProductFeatureSnapshot {
  profile: FeatureProfileCode;
  enabled: ProductFeatureCode[];
}

/** Loads the optional product areas this install runs. */
export async function fetchProductFeatures(): Promise<ProductFeatureSnapshot> {
  const response = unwrapGenerated<ProductFeaturesResponse>(
    await getProductFeatures(),
    "Failed to load enabled features",
  );
  return {
    profile: response.profile as FeatureProfileCode,
    enabled: [...response.enabledFeatures] as ProductFeatureCode[],
  };
}
