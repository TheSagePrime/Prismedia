<script lang="ts">
  /**
   * One tile in a folder view: a folder, photo, or video with its preview thumbnail. Folders show
   * the thumbnail of the first photo or video inside them. Used by the Files folder grid and the
   * home page's Folders row so both look the same.
   */
  import { FileText, Folder, Image as ImageIcon, Video } from "@lucide/svelte";
  import { cn } from "@prismedia/ui-svelte";

  interface Props {
    name: string;
    /** Whether this tile is a folder (otherwise a file). */
    directory: boolean;
    thumbnailUrl?: string | null;
    mimeType?: string | null;
    /** Short secondary line, such as a folder path. */
    subtitle?: string | null;
    /** Link target; renders an anchor when set. */
    href?: string;
    /** Click handler; renders a button when set and no href is given. */
    onclick?: () => void;
    selected?: boolean;
    class?: string;
  }

  let { name, directory, thumbnailUrl = null, mimeType = null, subtitle = null, href, onclick, selected = false, class: className }: Props = $props();

  let imageFailed = $state(false);
  $effect(() => {
    void thumbnailUrl;
    imageFailed = false;
  });

  const FallbackIcon = $derived(
    directory ? Folder : mimeType?.startsWith("video/") ? Video : mimeType?.startsWith("image/") ? ImageIcon : FileText,
  );
  const showImage = $derived(Boolean(thumbnailUrl) && !imageFailed);
</script>

{#snippet body()}
  <span class="tile-art" class:has-image={showImage}>
    {#if showImage}
      <img src={thumbnailUrl} alt="" loading="lazy" decoding="async" referrerpolicy="no-referrer" onerror={() => (imageFailed = true)} />
    {:else}
      <FallbackIcon class="fallback-icon" aria-hidden="true" />
    {/if}
    {#if directory}
      <span class="folder-badge" aria-hidden="true"><Folder class="h-3 w-3" /></span>
    {/if}
  </span>
  <span class="tile-text">
    <span class="tile-name" title={name}>{name}</span>
    {#if subtitle}<span class="tile-subtitle">{subtitle}</span>{/if}
  </span>
{/snippet}

{#if href}
  <a {href} class={cn("folder-tile", className)} class:selected aria-label={directory ? `Folder ${name}` : name}>
    {@render body()}
  </a>
{:else}
  <button type="button" class={cn("folder-tile", className)} class:selected aria-label={directory ? `Folder ${name}` : name} {onclick}>
    {@render body()}
  </button>
{/if}

<style>
  .folder-tile {
    display: grid;
    gap: 0.4rem;
    min-width: 0;
    padding: 0;
    border: 0;
    background: none;
    color: inherit;
    text-align: left;
    text-decoration: none;
    cursor: pointer;
  }

  .tile-art {
    position: relative;
    display: grid;
    place-items: center;
    aspect-ratio: 1;
    overflow: hidden;
    border: 1px solid var(--color-border-subtle);
    border-radius: var(--radius-sm);
    background: var(--color-surface-2);
    color: var(--color-text-disabled);
    transition: border-color var(--duration-fast) var(--ease-default);
  }

  .tile-art img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }

  .tile-art :global(.fallback-icon) {
    width: 2rem;
    height: 2rem;
  }

  .folder-badge {
    position: absolute;
    left: 0.35rem;
    bottom: 0.35rem;
    display: grid;
    place-items: center;
    width: 1.4rem;
    height: 1.4rem;
    border-radius: var(--radius-xs);
    background: rgb(0 0 0 / 0.6);
    color: var(--color-text-primary);
  }

  .folder-tile:hover .tile-art,
  .folder-tile:focus-visible .tile-art,
  .folder-tile.selected .tile-art {
    border-color: var(--color-border-accent);
  }

  .folder-tile:focus-visible {
    outline: none;
  }

  .tile-text {
    display: grid;
    min-width: 0;
  }

  .tile-name,
  .tile-subtitle {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .tile-name {
    color: var(--color-text-primary);
    font: 500 0.78rem/1.3 var(--font-inter, Inter), sans-serif;
  }

  .tile-subtitle {
    color: var(--color-text-muted);
    font: 500 0.66rem/1.3 var(--font-jetbrains-mono, "JetBrains Mono"), monospace;
  }
</style>
