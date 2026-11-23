<script lang="ts">
  /**
   * Window-wide drop target for uploads. While files from the computer are dragged anywhere over
   * the page, a clear overlay names the destination folder (or explains that it is read-only), and
   * dropping hands the files to the upload queue. Enter/leave events are counted so the overlay
   * never flickers, and the animation is skipped when the system asks for reduced motion.
   */
  import { CloudUpload, Lock } from "@lucide/svelte";
  import { fadeQuick, prefersReducedMotion, scaleIn } from "@prismedia/ui-svelte";
  import { DragDepthTracker, isFileDrag } from "$lib/files/drag-tracker";

  interface Props {
    /** Name of the folder that will receive the files, or null when none is chosen. */
    targetName: string | null;
    /** Whether the target refuses uploads (externally managed library). */
    readOnly?: boolean;
    /** Receives the dropped files. */
    onDrop: (dataTransfer: DataTransfer) => void;
  }

  let { targetName, readOnly = false, onDrop }: Props = $props();

  const tracker = new DragDepthTracker();
  let active = $state(false);
  const reduceMotion = prefersReducedMotion();
  const motion = { duration: reduceMotion ? 0 : undefined };
  const canDrop = $derived(Boolean(targetName) && !readOnly);

  function handleEnter(event: DragEvent): void {
    if (!isFileDrag(event.dataTransfer)) return;
    event.preventDefault();
    active = tracker.enter();
  }

  function handleOver(event: DragEvent): void {
    if (!isFileDrag(event.dataTransfer)) return;
    // Cancelling dragover is what makes the whole window a valid drop target.
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = canDrop ? "copy" : "none";
    if (!active) active = tracker.enter();
  }

  function handleLeave(event: DragEvent): void {
    if (!isFileDrag(event.dataTransfer)) return;
    active = tracker.leave();
  }

  function handleDrop(event: DragEvent): void {
    if (!isFileDrag(event.dataTransfer)) return;
    event.preventDefault();
    active = tracker.reset();
    if (canDrop && event.dataTransfer) onDrop(event.dataTransfer);
  }

  function handleKey(event: KeyboardEvent): void {
    if (event.key === "Escape" && active) active = tracker.reset();
  }
</script>

<svelte:window
  ondragenter={handleEnter}
  ondragover={handleOver}
  ondragleave={handleLeave}
  ondrop={handleDrop}
  ondragend={() => (active = tracker.reset())}
  onkeydown={handleKey}
/>

{#if active}
  <div class="drop-overlay" data-can-drop={canDrop} transition:fadeQuick={motion} role="status" aria-live="assertive">
    <div class="drop-card floating-surface" transition:scaleIn={motion}>
      <span class="drop-icon" class:pulse={canDrop && !reduceMotion}>
        {#if canDrop}<CloudUpload class="h-7 w-7" />{:else}<Lock class="h-7 w-7" />{/if}
      </span>
      {#if canDrop}
        <p class="drop-title">Drop files or folders to upload</p>
        <p class="drop-detail">to <strong>{targetName}</strong></p>
      {:else if targetName}
        <p class="drop-title">Uploads aren't allowed here</p>
        <p class="drop-detail"><strong>{targetName}</strong> is managed by a connected app and is read-only.</p>
      {:else}
        <p class="drop-title">Choose a folder first</p>
        <p class="drop-detail">Select a folder, then drop your files.</p>
      {/if}
    </div>
  </div>
{/if}

<style>
  .drop-overlay {
    position: fixed;
    inset: 0;
    z-index: 90;
    display: grid;
    place-items: center;
    padding: 1rem;
    background: rgb(0 0 0 / 0.55);
    pointer-events: none;
  }

  .drop-overlay::before {
    content: "";
    position: absolute;
    inset: max(0.75rem, env(safe-area-inset-top)) 0.75rem max(0.75rem, env(safe-area-inset-bottom));
    border: 2px dashed var(--color-border-accent-strong, var(--color-border-accent));
    border-radius: var(--radius-lg);
  }

  .drop-overlay[data-can-drop="false"]::before {
    border-color: var(--color-border-default);
  }

  .drop-card {
    display: grid;
    justify-items: center;
    gap: 0.4rem;
    width: min(24rem, 100%);
    padding: 1.5rem 1.25rem;
    text-align: center;
  }

  .drop-icon {
    display: grid;
    place-items: center;
    width: 3.25rem;
    height: 3.25rem;
    margin-bottom: 0.35rem;
    border: 1px solid var(--color-border-default);
    border-radius: var(--radius-md);
    background: var(--color-surface-3);
    color: var(--color-text-primary);
  }

  .drop-icon.pulse {
    animation: drop-pulse 1.4s var(--ease-default, ease-in-out) infinite;
  }

  @keyframes drop-pulse {
    0%,
    100% {
      transform: translateY(0);
    }
    50% {
      transform: translateY(-3px);
    }
  }

  .drop-title,
  .drop-detail {
    margin: 0;
  }

  .drop-title {
    color: var(--color-text-primary);
    font: 600 1rem/1.35 var(--font-geist, Geist), sans-serif;
  }

  .drop-detail {
    color: var(--color-text-secondary);
    font: 500 0.82rem/1.4 var(--font-inter, Inter), sans-serif;
    overflow-wrap: anywhere;
  }

  .drop-detail strong {
    color: var(--color-text-primary);
    font-weight: 600;
  }

  @media (prefers-reduced-motion: reduce) {
    .drop-icon.pulse {
      animation: none;
    }
  }
</style>
