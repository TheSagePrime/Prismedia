<script lang="ts">
  import { Film } from "@lucide/svelte";
  import EntityIndexPage from "$lib/components/entities/EntityIndexPage.svelte";
  import { ENTITY_KIND } from "$lib/entities/entity-codes";
  import { productFeatures } from "$lib/features/product-features.svelte";
  import { videoListKinds } from "$lib/features/product-features";

  // With movies and series switched off, videos the scanner filed as movies or episodes are listed
  // here too, so every video on disk stays reachable.
  const listKinds = videoListKinds(productFeatures.enabled);
</script>

<EntityIndexPage
  kind={ENTITY_KIND.video}
  title="Videos"
  icon={Film}
  prefsKey="videos"
  emptyTitle="No videos"
  emptyMessage="No videos in your library yet. Add a library root and scan to get started."
  errorMessage="Failed to load videos."
  initialMediaWall
  enableFeedView
  listKind={listKinds.join(",")}
/>
