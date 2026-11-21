<script lang="ts">
  /**
   * Chooses a destination folder for moving several Files entries at once. Only folders the tree
   * has already loaded and that are writable are offered; the dialog shows progress while the
   * moves run one by one.
   */
  import { Loader2, X } from "@lucide/svelte";
  import { Button, Dialog, SearchableSelect, type SearchableSelectOption } from "@prismedia/ui-svelte";

  interface Props {
    open: boolean;
    itemCount: number;
    folders: SearchableSelectOption[];
    /** Moves the selection into the chosen folder's tree path. */
    onConfirm: (targetTreePath: string, onProgress: (done: number, total: number) => void) => Promise<void>;
    onClose: () => void;
  }

  let { open, itemCount, folders, onConfirm, onClose }: Props = $props();

  let target = $state<string | undefined>(undefined);
  let busy = $state(false);
  let progress = $state<{ done: number; total: number } | null>(null);

  $effect(() => {
    if (open) {
      target = undefined;
      busy = false;
      progress = null;
    }
  });

  async function confirm(): Promise<void> {
    if (!target || busy) return;
    busy = true;
    try {
      await onConfirm(target, (done, total) => (progress = { done, total }));
      onClose();
    } finally {
      busy = false;
    }
  }
</script>

<Dialog {open} {onClose} ariaLabel="Move items" dismissible={!busy} class="w-[min(92vw,28rem)]">
  <div class="flex flex-col gap-4 p-5">
    <div class="flex items-start justify-between gap-4">
      <h2 class="font-heading text-base font-semibold tracking-wide text-text-primary">
        Move {itemCount.toLocaleString()} {itemCount === 1 ? "item" : "items"}
      </h2>
      <Button variant="ghost" size="icon" type="button" onclick={onClose} disabled={busy} class="shrink-0" aria-label="Cancel">
        <X class="h-4 w-4" />
      </Button>
    </div>

    <SearchableSelect
      options={folders}
      value={target}
      label="Destination folder"
      searchLabel="Search folders"
      placeholder="Choose a folder…"
      emptyText="No loaded folder matches. Open the folder in the tree first."
      disabled={busy}
      onchange={(value) => (target = value)}
    />
    <p class="text-body-sm text-text-muted">Only folders that are open in the tree are listed.</p>

    {#if progress}
      <p class="text-body-sm text-text-secondary" role="status">
        Moved {progress.done.toLocaleString()} of {progress.total.toLocaleString()}…
      </p>
    {/if}

    <div class="flex justify-end gap-2">
      <Button type="button" variant="ghost" size="md" onclick={onClose} disabled={busy}>Cancel</Button>
      <Button type="button" variant="primary" size="md" disabled={!target || busy} onclick={() => void confirm()}>
        {#if busy}<Loader2 class="h-4 w-4 animate-spin" />{/if}
        Move here
      </Button>
    </div>
  </div>
</Dialog>
