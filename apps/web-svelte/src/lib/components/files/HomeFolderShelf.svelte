<script lang="ts">
  /**
   * Home page row of top-level folders in every library, each with a preview thumbnail from the
   * photos or videos inside it. Tapping a folder opens it in the Folders (Files) view.
   */
  import { onMount } from "svelte";
  import { FolderTree } from "@lucide/svelte";
  import { buttonVariants } from "@prismedia/ui-svelte";
  import { fetchFileChildren, fetchFileRoots, type FileEntry } from "$lib/api/files";
  import { FILE_ENTRY_KIND } from "$lib/api/generated/codes";
  import FolderTile from "./FolderTile.svelte";

  interface Props {
    /** Most folders to show. */
    limit?: number;
  }

  let { limit = 24 }: Props = $props();

  interface FolderItem {
    entry: FileEntry;
    rootLabel: string;
  }

  let folders = $state<FolderItem[]>([]);
  let loaded = $state(false);
  let multipleRoots = $state(false);

  function folderHref(entry: FileEntry): string {
    const params = new URLSearchParams({ rootId: entry.rootId, path: entry.path });
    return `/files?${params.toString()}`;
  }

  onMount(async () => {
    try {
      const { roots } = await fetchFileRoots();
      multipleRoots = roots.length > 1;
      const listings = await Promise.allSettled(roots.map(async (root) => ({
        root,
        entries: (await fetchFileChildren(root.id, "")).entries,
      })));
      folders = listings
        .flatMap((result) => result.status === "fulfilled"
          ? result.value.entries
            .filter((entry) => entry.kind === FILE_ENTRY_KIND.directory && !entry.excluded)
            .map((entry) => ({ entry, rootLabel: result.value.root.label }))
          : [])
        .slice(0, limit);
    } catch {
      folders = [];
    } finally {
      loaded = true;
    }
  });
</script>

{#if loaded && folders.length > 0}
  <section class="px-3" aria-labelledby="home-folders-heading">
    <div class="mb-4 flex items-center justify-between">
      <h2 id="home-folders-heading" class="flex items-center gap-2 text-lg font-semibold">
        <FolderTree class="h-4.5 w-4.5" />
        Folders
      </h2>
      <a href="/files" class={buttonVariants({ variant: "ghost", size: "sm" })}>Browse all</a>
    </div>
    <div class="folder-row">
      {#each folders as { entry, rootLabel } (`${entry.rootId}:${entry.path}`)}
        <FolderTile
          name={entry.name}
          directory
          thumbnailUrl={entry.thumbnailUrl}
          subtitle={multipleRoots ? rootLabel : null}
          href={folderHref(entry)}
        />
      {/each}
    </div>
  </section>
{/if}

<style>
  .folder-row {
    display: grid;
    grid-auto-columns: clamp(120px, 14vw, 170px);
    grid-auto-flow: column;
    gap: 0.75rem;
    overflow-x: auto;
    padding-bottom: 0.5rem;
    scrollbar-width: thin;
  }
</style>
