<script lang="ts">
  /**
   * Confirms an upload batch and asks what to do with files whose names already exist in the
   * destination: skip them, keep both under a numbered name, or report them as errors. The choice
   * applies to every file in the batch; other files continue either way.
   */
  import { Upload, X } from "@lucide/svelte";
  import { Button, ChoiceGroup, Dialog, type ChoiceOption } from "@prismedia/ui-svelte";
  import { FILE_UPLOAD_CONFLICT_POLICY, type FileUploadConflictPolicyCode } from "$lib/api/generated/codes";
  import { formatBytes } from "$lib/utils/format";

  interface Props {
    open: boolean;
    fileCount: number;
    totalBytes: number;
    destination: string;
    conflictPolicy: FileUploadConflictPolicyCode;
    onConflictPolicyChange: (policy: FileUploadConflictPolicyCode) => void;
    onConfirm: () => void;
    onClose: () => void;
  }

  let {
    open,
    fileCount,
    totalBytes,
    destination,
    conflictPolicy,
    onConflictPolicyChange,
    onConfirm,
    onClose,
  }: Props = $props();

  let confirmButton = $state<HTMLButtonElement | null>(null);

  const options: ChoiceOption<FileUploadConflictPolicyCode>[] = [
    { value: FILE_UPLOAD_CONFLICT_POLICY.keepBoth, label: "Keep both" },
    { value: FILE_UPLOAD_CONFLICT_POLICY.skip, label: "Skip" },
    { value: FILE_UPLOAD_CONFLICT_POLICY.fail, label: "Report as error" },
  ];

  const explanation = $derived(
    conflictPolicy === FILE_UPLOAD_CONFLICT_POLICY.keepBoth
      ? "The existing file stays. The new one is saved with a number, like “photo (1).jpg”."
      : conflictPolicy === FILE_UPLOAD_CONFLICT_POLICY.skip
        ? "The existing file stays and the new one is not uploaded."
        : "The existing file stays and the new one is marked as failed. Other files still upload.",
  );
</script>

<Dialog {open} {onClose} initialFocus={() => confirmButton} ariaLabel="Upload files" class="w-[min(92vw,28rem)]">
  <div class="flex flex-col gap-4 p-5">
    <div class="flex items-start justify-between gap-4">
      <div class="min-w-0">
        <h2 class="font-heading text-base font-semibold tracking-wide text-text-primary">
          Upload {fileCount.toLocaleString()} {fileCount === 1 ? "file" : "files"}
        </h2>
        <p class="mt-1 truncate font-mono text-xs text-text-muted">
          {formatBytes(totalBytes)} to {destination}
        </p>
      </div>
      <Button variant="ghost" size="icon" type="button" onclick={onClose} class="shrink-0" aria-label="Cancel">
        <X class="h-4 w-4" />
      </Button>
    </div>

    <div class="flex flex-col gap-2">
      <span class="text-label text-text-muted">If a file with the same name already exists</span>
      <ChoiceGroup
        type="single"
        ariaLabel="When a file with the same name exists"
        {options}
        value={conflictPolicy}
        onValueChange={onConflictPolicyChange}
      />
      <p class="text-body-sm text-text-secondary">{explanation}</p>
    </div>

    <p class="text-body-sm text-text-muted">
      Files upload one piece at a time and resume on their own if the connection drops. Keep this page open
      until they finish.
    </p>

    <div class="flex justify-end gap-2">
      <Button type="button" variant="ghost" size="md" onclick={onClose}>Cancel</Button>
      <Button bind:ref={confirmButton} type="button" variant="primary" size="md" onclick={onConfirm}>
        <Upload class="h-4 w-4" />Upload
      </Button>
    </div>
  </div>
</Dialog>
