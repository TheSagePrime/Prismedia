<script lang="ts">
  /**
   * Floating progress panel for Files uploads: overall bytes, speed, and time left, then one row
   * per file with its own progress, retry and cancel controls, and the outcome of the batch's
   * conflict choice (skipped or renamed files). Every control is a visible button, never hover-only.
   */
  import { ChevronDown, ChevronUp, RotateCcw, Upload, X } from "@lucide/svelte";
  import { Badge, Button, Meter } from "@prismedia/ui-svelte";
  import { FILE_UPLOAD_OUTCOME } from "$lib/api/generated/codes";
  import type { UploadItemSnapshot, UploadQueueSnapshot } from "$lib/files/upload-queue";
  import { formatBytes, formatEta, formatSpeed } from "$lib/utils/format";

  interface Props {
    snapshot: UploadQueueSnapshot;
    onCancel: (id: string) => void;
    onCancelAll: () => void;
    onRetry: (id: string) => void;
    onRetryFailed: () => void;
    onClearFinished: () => void;
    onDismiss: () => void;
    /** Lifts the panel above the folder-download status when both are shown. */
    raised?: boolean;
    /** Dropped folders are still being read; their files join the list shortly. */
    preparing?: boolean;
  }

  let { snapshot, onCancel, onCancelAll, onRetry, onRetryFailed, onClearFinished, onDismiss, raised = false, preparing = false }: Props = $props();

  /** Rendering stays bounded for drops of thousands of files; totals still cover every file. */
  const VISIBLE_ROWS = 200;
  let expanded = $state(true);

  const counts = $derived(snapshot.counts);
  const finished = $derived(counts.done + counts.failed + counts.cancelled);
  const total = $derived(snapshot.items.length);
  const heading = $derived(
    preparing && total === 0
      ? "Reading dropped files…"
      : snapshot.active
      ? `Uploading ${Math.min(finished + 1, total).toLocaleString()} of ${total.toLocaleString()}`
      : counts.failed > 0
        ? `${counts.failed.toLocaleString()} upload${counts.failed === 1 ? "" : "s"} need attention`
        : "Uploads finished",
  );
  const detail = $derived(
    [
      `${formatBytes(snapshot.uploadedBytes)} of ${formatBytes(snapshot.totalBytes)}`,
      snapshot.active ? formatSpeed(snapshot.bytesPerSecond) : null,
      snapshot.active && snapshot.etaSeconds !== null ? `${formatEta(snapshot.etaSeconds)} left` : null,
    ].filter(Boolean).join(" · "),
  );
  const visibleItems = $derived(orderForDisplay(snapshot.items).slice(0, VISIBLE_ROWS));

  /** Failed and in-progress files first, so what needs attention is always on screen. */
  function orderForDisplay(items: UploadItemSnapshot[]): UploadItemSnapshot[] {
    const rank = (item: UploadItemSnapshot) =>
      item.status === "failed" ? 0
        : item.status === "uploading" || item.status === "retrying" || item.status === "completing" ? 1
          : item.status === "queued" ? 2
            : 3;
    return [...items].sort((a, b) => rank(a) - rank(b));
  }

  function statusLabel(item: UploadItemSnapshot): string {
    switch (item.status) {
      case "queued":
        return "Waiting";
      case "uploading":
        return `${formatBytes(item.uploadedBytes)} of ${formatBytes(item.sizeBytes)}`;
      case "retrying":
        return `Connection lost — retrying (attempt ${item.attempt})`;
      case "completing":
        return "Saving…";
      case "failed":
        return item.error ?? "Failed";
      case "cancelled":
        return "Cancelled";
      case "done":
        if (item.outcome === FILE_UPLOAD_OUTCOME.skipped) return "Skipped — a file with this name already exists";
        if (item.outcome === FILE_UPLOAD_OUTCOME.renamed) return `Saved as ${item.finalPath?.split("/").at(-1) ?? item.name}`;
        return "Uploaded";
    }
  }

  function folderOf(item: UploadItemSnapshot): string {
    const path = [item.targetPath, item.relativePath].filter(Boolean).join("/");
    const folder = path.split("/").slice(0, -1).join("/");
    return folder ? `/${folder}` : "/";
  }
</script>

<aside class="upload-panel floating-surface" class:raised aria-label="Uploads">
  <div class="panel-heading">
    <span class="panel-icon"><Upload class="h-4 w-4" /></span>
    <div class="heading-text" aria-live="polite" aria-atomic="true">
      <p class="kicker">Uploads</p>
      <h2>{heading}</h2>
    </div>
    <Button
      variant="ghost"
      size="icon-sm"
      type="button"
      aria-expanded={expanded}
      aria-label={expanded ? "Collapse upload list" : "Expand upload list"}
      onclick={() => (expanded = !expanded)}
    >
      {#if expanded}<ChevronDown class="h-4 w-4" />{:else}<ChevronUp class="h-4 w-4" />{/if}
    </Button>
    {#if !snapshot.active && !preparing}
      <Button variant="ghost" size="icon-sm" type="button" aria-label="Close uploads" onclick={onDismiss}>
        <X class="h-4 w-4" />
      </Button>
    {/if}
  </div>

  <Meter value={snapshot.uploadedBytes} max={Math.max(snapshot.totalBytes, 1)} showValue label="Overall progress" />
  <p class="detail">{detail}</p>

  {#if counts.failed || counts.skipped || counts.renamed || counts.cancelled}
    <div class="summary">
      {#if counts.failed}<Badge variant="error">{counts.failed.toLocaleString()} failed</Badge>{/if}
      {#if counts.skipped}<Badge variant="warning">{counts.skipped.toLocaleString()} skipped</Badge>{/if}
      {#if counts.renamed}<Badge variant="info">{counts.renamed.toLocaleString()} renamed</Badge>{/if}
      {#if counts.cancelled}<Badge>{counts.cancelled.toLocaleString()} cancelled</Badge>{/if}
    </div>
  {/if}

  <div class="actions">
    {#if counts.failed}
      <Button variant="secondary" size="sm" type="button" onclick={onRetryFailed}><RotateCcw class="h-3.5 w-3.5" />Retry failed</Button>
    {/if}
    {#if snapshot.active}
      <Button variant="ghost" size="sm" type="button" onclick={onCancelAll}>Cancel all</Button>
    {/if}
    {#if counts.done || counts.cancelled}
      <Button variant="ghost" size="sm" type="button" onclick={onClearFinished}>Clear finished</Button>
    {/if}
  </div>

  {#if expanded}
    <ul class="file-list">
      {#each visibleItems as item (item.id)}
        <li class="file-row" data-status={item.status}>
          <div class="file-text">
            <span class="file-name" title={item.relativePath}>{item.name}</span>
            <span class="file-folder">{folderOf(item)}</span>
            <span class="file-status">{statusLabel(item)}</span>
            {#if item.status === "uploading" || item.status === "retrying" || item.status === "completing"}
              <Meter value={item.uploadedBytes} max={Math.max(item.sizeBytes, 1)} class="file-meter" />
            {/if}
          </div>
          {#if item.status === "failed" || item.status === "cancelled"}
            <Button variant="ghost" size="icon-sm" type="button" aria-label={`Retry ${item.name}`} onclick={() => onRetry(item.id)}>
              <RotateCcw class="h-3.5 w-3.5" />
            </Button>
          {:else if item.status !== "done"}
            <Button variant="ghost" size="icon-sm" type="button" aria-label={`Cancel ${item.name}`} onclick={() => onCancel(item.id)}>
              <X class="h-3.5 w-3.5" />
            </Button>
          {/if}
        </li>
      {/each}
      {#if snapshot.items.length > VISIBLE_ROWS}
        <li class="more">+{(snapshot.items.length - VISIBLE_ROWS).toLocaleString()} more files</li>
      {/if}
    </ul>
  {/if}
</aside>

<style>
  .upload-panel {
    position: fixed;
    z-index: 80;
    right: max(1rem, env(safe-area-inset-right));
    bottom: max(1rem, env(safe-area-inset-bottom));
    display: grid;
    width: min(24rem, calc(100vw - 2rem));
    max-height: min(32rem, calc(100dvh - 6rem));
    gap: 0.6rem;
    padding: 0.9rem 1rem;
    border-left: 2px solid var(--color-border-accent);
    grid-template-rows: auto auto auto auto auto minmax(0, 1fr);
  }

  .upload-panel.raised {
    bottom: calc(max(1rem, env(safe-area-inset-bottom)) + 9.5rem);
    max-height: min(24rem, calc(100dvh - 16rem));
  }

  .panel-heading {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
  }

  .panel-icon {
    display: grid;
    width: 2rem;
    height: 2rem;
    flex: 0 0 auto;
    place-items: center;
    border: 1px solid var(--color-border-default);
    border-radius: var(--radius-sm);
    color: var(--color-text-secondary);
    background: var(--color-surface-3);
  }

  .heading-text {
    flex: 1;
    min-width: 0;
  }

  .kicker,
  .detail,
  h2 {
    margin: 0;
  }

  .kicker {
    color: var(--color-text-muted);
    font: 600 0.65rem/1.2 var(--font-inter, Inter), sans-serif;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  h2 {
    overflow: hidden;
    color: var(--color-text-primary);
    font: 600 0.9rem/1.35 var(--font-geist, Geist), sans-serif;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .detail {
    color: var(--color-text-muted);
    font: 500 0.7rem/1.35 var(--font-jetbrains-mono, "JetBrains Mono"), monospace;
  }

  .summary,
  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
  }

  .actions:empty,
  .summary:empty {
    display: none;
  }

  .file-list {
    display: grid;
    min-height: 0;
    margin: 0;
    padding: 0;
    overflow-y: auto;
    list-style: none;
    border-top: 1px solid var(--color-border-subtle);
  }

  .file-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
    padding: 0.45rem 0;
    border-bottom: 1px solid var(--color-border-subtle);
  }

  .file-text {
    display: grid;
    flex: 1;
    min-width: 0;
    gap: 0.1rem;
  }

  .file-name,
  .file-folder,
  .file-status {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .file-name {
    color: var(--color-text-primary);
    font: 500 0.78rem/1.3 var(--font-inter, Inter), sans-serif;
  }

  .file-folder,
  .file-status {
    color: var(--color-text-muted);
    font: 500 0.66rem/1.3 var(--font-jetbrains-mono, "JetBrains Mono"), monospace;
  }

  .file-row[data-status="failed"] .file-status {
    color: var(--color-error-text);
    white-space: normal;
  }

  .file-row[data-status="done"] .file-name,
  .file-row[data-status="cancelled"] .file-name {
    color: var(--color-text-secondary);
  }

  .file-text :global(.file-meter) {
    margin-top: 0.2rem;
  }

  .more {
    padding: 0.45rem 0;
    color: var(--color-text-muted);
    font: 500 0.7rem/1.3 var(--font-inter, Inter), sans-serif;
  }

  @media (max-width: 767px) {
    .upload-panel {
      left: max(0.75rem, env(safe-area-inset-left));
      right: max(0.75rem, env(safe-area-inset-right));
      bottom: max(0.75rem, env(safe-area-inset-bottom));
      width: auto;
    }
  }
</style>
