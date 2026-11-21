<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import FileDetailPane from "$lib/components/files/FileDetailPane.svelte";
  import FileDownloadStatus from "$lib/components/files/FileDownloadStatus.svelte";
  import FileMoveDialog from "$lib/components/files/FileMoveDialog.svelte";
  import FileTreePane from "$lib/components/files/FileTreePane.svelte";
  import FileUploadDialog from "$lib/components/files/FileUploadDialog.svelte";
  import FileUploadPanel from "$lib/components/files/FileUploadPanel.svelte";
  import ConfirmDialog from "$lib/components/entities/ConfirmDialog.svelte";
  import NameInputDialog from "$lib/components/entities/NameInputDialog.svelte";
  import {
    createFileFolder as apiCreateFileFolder,
    deleteFile as apiDeleteFile,
    excludeFile as apiExcludeFile,
    fetchFileChildren,
    fetchFileDetail,
    fetchFileRoots,
    fileArchiveDownloadUrl,
    fileDownloadUrl,
    moveFile as apiMoveFile,
    prepareFolderArchiveDownload,
    renameFile as apiRenameFile,
    removeFileExclusion as apiRemoveFileExclusion,
    rescanFileRoot as apiRescanFileRoot,
    createFileUploadTransport,
    type FileDetail,
    type FileRoot,
    type FileUploadItem,
  } from "$lib/api/files";
  import {
    FILE_ENTRY_KIND,
    FILE_UPLOAD_CONFLICT_POLICY,
    type FileUploadConflictPolicyCode,
  } from "$lib/api/generated/codes";
  import { refreshEntity } from "$lib/api/entities";
  import { fileContextActions, type FileActionId } from "$lib/files/file-actions";
  import {
    createFileTreeRegistry,
    fileTreeRootPath,
    reconcileFileTreeEntries,
    type FileTreeNodeMeta,
    upsertFileTreeEntries,
  } from "$lib/files/file-tree-state";
  import { collectDroppedFiles, fileListToUploadItems } from "$lib/files/dropped-files";
  import { UploadQueue, type UploadQueueSnapshot } from "$lib/files/upload-queue";
  import { MEDIA_PICKER_ACCEPT, supportsFolderPicker } from "$lib/files/upload-pickers";
  import { planBulkMove, runBulkOperation, summarizeBulkResult, topLevelSelection } from "$lib/files/bulk-operations";
  import type { EntityGridBulkAction } from "$lib/entities/entity-grid";
  import { useNsfw } from "$lib/nsfw/store.svelte";

  interface PendingNameDialog {
    title: string;
    initialValue?: string;
    confirmLabel: string;
    onConfirm: (value: string) => Promise<void>;
  }

  interface PendingConfirmation {
    title: string;
    message: string;
    confirmLabel: string;
    danger?: boolean;
    onConfirm: () => Promise<void>;
  }

  interface DownloadActivity {
    fileName: string;
    message: string;
    detail: string;
    progressPercent: number | null;
  }

  let roots = $state<FileRoot[]>([]);
  let registry = $state(new Map<string, FileTreeNodeMeta>());
  let treePaths = $state<string[]>([]);
  let loadedKeys = $state(new Set<string>());
  let selectedTreePath = $state<string | null>(null);
  let detail = $state<FileDetail | null>(null);
  let treeSearch = $state("");
  let loadingTree = $state(false);
  let loadingDetail = $state(false);
  let error = $state<string | null>(null);
  let mobileDetail = $state(false);
  let treeWidthPx = $state(320);
  let resizing = $state(false);
  let syncedQueryKey = "";
  let lastNsfwMode = $state<string | null>(null);
  let pendingNameDialog = $state<PendingNameDialog | null>(null);
  let pendingConfirmation = $state<PendingConfirmation | null>(null);
  let downloadActivity = $state<DownloadActivity | null>(null);
  const nsfw = useNsfw();

  const selectedMeta = $derived(selectedTreePath ? registry.get(selectedTreePath) ?? null : null);

  function loadedKey(meta: Pick<FileTreeNodeMeta, "rootId" | "path">): string {
    return `${meta.rootId}:${meta.path}`;
  }

  function parentPath(path: string): string {
    return path.split("/").filter(Boolean).slice(0, -1).join("/");
  }

  function basename(path: string): string {
    return path.split("/").filter(Boolean).at(-1) ?? path;
  }

  function directoryTarget(meta: FileTreeNodeMeta | null): FileTreeNodeMeta | null {
    if (!meta) return roots[0] ? registry.get(`${fileTreeRootPath(roots[0], roots)}/`) ?? null : null;
    if (meta.kind === "directory") return meta;
    const parent = parentPath(meta.path);
    return [...registry.values()].find((candidate) => candidate.rootId === meta.rootId && candidate.path === parent) ?? meta;
  }

  async function loadRoots(): Promise<void> {
    loadingTree = true;
    error = null;
    try {
      const response = await fetchFileRoots();
      roots = response.roots;
      const nextRegistry = createFileTreeRegistry(roots);
      const rootTreePaths = roots.map((r) => `${fileTreeRootPath(r, roots)}/`);
      const allPaths = [...rootTreePaths];
      const nextLoadedKeys = new Set<string>();

      const results = await Promise.allSettled(roots.map(async (root) => {
        const rootTreePath = `${fileTreeRootPath(root, roots)}/`;
        const children = await fetchFileChildren(root.id, "");
        return { rootTreePath, root, children };
      }));

      for (const result of results) {
        if (result.status !== "fulfilled") continue;
        const { rootTreePath, root, children } = result.value;
        const childPaths = upsertFileTreeEntries(nextRegistry, rootTreePath, children.entries);
        allPaths.push(...childPaths);
        nextLoadedKeys.add(`${root.id}:`);
      }

      registry = nextRegistry;
      treePaths = [...new Set(allPaths)];
      loadedKeys = nextLoadedKeys;
    } catch (loadError) {
      error = loadError instanceof Error ? loadError.message : "Failed to load watched roots";
    } finally {
      loadingTree = false;
    }
  }

  async function loadChildren(meta: FileTreeNodeMeta): Promise<void> {
    const key = loadedKey(meta);
    if (loadedKeys.has(key)) return;
    loadingTree = true;
    try {
      const response = await fetchFileChildren(meta.rootId, meta.path);
      const rootTreePath = [...registry.values()].find((candidate) => candidate.rootId === meta.rootId && candidate.path === "")?.treePath;
      if (!rootTreePath) return;
      const nextRegistry = new Map(registry);
      const reconciled = reconcileFileTreeEntries(nextRegistry, treePaths, loadedKeys, rootTreePath, meta, response.entries);
      const childPaths = reconciled.childPaths;
      registry = nextRegistry;
      treePaths = reconciled.treePaths;
      loadedKeys = new Set([...reconciled.loadedKeys, key]);
    } catch (loadError) {
      error = loadError instanceof Error ? loadError.message : "Failed to load folder";
    } finally {
      loadingTree = false;
    }
  }

  async function loadDetail(meta: FileTreeNodeMeta): Promise<void> {
    loadingDetail = true;
    error = null;
    try {
      detail = await fetchFileDetail(meta.rootId, meta.path);
    } catch (loadError) {
      detail = null;
      error = loadError instanceof Error ? loadError.message : "Failed to load file details";
    } finally {
      loadingDetail = false;
    }
  }

  async function selectTreePath(treePath: string, options: { replaceUrl?: boolean; showDetail?: boolean } = {}): Promise<void> {
    const meta = registry.get(treePath);
    if (!meta) return;
    selectedTreePath = treePath;

    const params = new URLSearchParams({ rootId: meta.rootId });
    if (meta.path) params.set("path", meta.path);
    await goto(`/files?${params.toString()}`, { replaceState: options.replaceUrl ?? false, noScroll: true, keepFocus: true });

    if (meta.kind === "file" && (options.showDetail ?? true)) mobileDetail = true;
    await loadDetail(meta);
  }

  async function refreshSelected(): Promise<void> {
    const selected = selectedMeta;
    if (!selected) return;
    const target = selected.kind === "directory" ? selected : directoryTarget(selected);
    if (target) {
      const nextLoaded = new Set(loadedKeys);
      nextLoaded.delete(loadedKey(target));
      loadedKeys = nextLoaded;
      await loadChildren(target);
    }
    await loadDetail(selected);
  }

  function createFolder(meta: FileTreeNodeMeta): void {
    pendingNameDialog = {
      title: "New folder",
      confirmLabel: "Create folder",
      onConfirm: async (name) => {
        await apiCreateFileFolder({ rootId: meta.rootId, parentPath: meta.path, name });
        await refreshSelected();
      },
    };
  }

  async function renameFile(meta: FileTreeNodeMeta, proposedName?: string): Promise<void> {
    if (meta.isReadOnly) { error = "This library is read-only."; return; }
    if (!meta.path) {
      error = "Library roots cannot be renamed here.";
      return;
    }

    const applyRename = async (name: string) => {
      if (name === meta.name) return;
      await apiRenameFile({ rootId: meta.rootId, path: meta.path, name });
      const parent = directoryTarget({ ...meta, kind: "file" });
      if (parent) loadedKeys = new Set([...loadedKeys].filter((key) => key !== loadedKey(parent)));
      selectedTreePath = null;
      await loadRoots();
    };

    if (proposedName != null) {
      const trimmed = proposedName.trim();
      if (trimmed) await applyRename(trimmed);
      return;
    }

    pendingNameDialog = {
      title: `Rename ${meta.name}`,
      initialValue: meta.name,
      confirmLabel: "Rename",
      onConfirm: applyRename,
    };
  }

  async function moveFile(meta: FileTreeNodeMeta, targetDirectoryTreePath?: string | null): Promise<void> {
    if (!meta.path) {
      error = "Library roots cannot be moved here.";
      return;
    }
    const targetDirectory = targetDirectoryTreePath ? registry.get(targetDirectoryTreePath) : null;
    if (meta.isReadOnly || targetDirectory?.isReadOnly) { error = "Externally managed library files cannot be moved here."; return; }
    const defaultPath = targetDirectory
      ? [targetDirectory.path, basename(meta.path)].filter(Boolean).join("/")
      : meta.path;
    const applyMove = async (targetPath: string) => {
      if (targetPath === meta.path) return;
      await apiMoveFile({
        sourceRootId: meta.rootId,
        sourcePath: meta.path,
        targetRootId: targetDirectory?.rootId ?? meta.rootId,
        targetPath,
      });
      selectedTreePath = null;
      await loadRoots();
    };

    if (targetDirectory) {
      await applyMove(defaultPath);
      return;
    }

    pendingNameDialog = {
      title: `Move ${meta.name}`,
      initialValue: defaultPath,
      confirmLabel: "Move",
      onConfirm: applyMove,
    };
  }

  async function deleteFile(meta: FileTreeNodeMeta): Promise<void> {
    if (!meta.path) {
      error = "Library roots cannot be deleted here.";
      return;
    }
    const message =
      meta.kind === "directory"
        ? `Permanently delete ${meta.name} and everything inside it?`
        : `Permanently delete ${meta.name}?`;
    pendingConfirmation = {
      title: `Delete ${meta.name}?`,
      message,
      confirmLabel: "Delete permanently",
      danger: true,
      onConfirm: async () => {
        await apiDeleteFile(meta.rootId, meta.path);
        selectedTreePath = null;
        detail = null;
        await loadRoots();
      },
    };
  }

  async function rescan(meta: FileTreeNodeMeta): Promise<void> {
    const linkedEntities = detail?.linkedEntities ?? [];
    await apiRescanFileRoot({ rootId: meta.rootId, path: meta.path || null });
    if (linkedEntities.length) {
      await Promise.allSettled(
        linkedEntities.map((linked) => refreshEntity(linked.entityId)),
      );
    }
    await refreshSelected();
  }

  async function excludePath(meta: FileTreeNodeMeta): Promise<void> {
    if (!meta.path) {
      error = "Library roots cannot be excluded here.";
      return;
    }
    await apiExcludeFile({ rootId: meta.rootId, path: meta.path });
    await refreshSelected();
  }

  async function removeExclusion(meta: FileTreeNodeMeta): Promise<void> {
    if (!meta.path) return;
    await apiRemoveFileExclusion({ rootId: meta.rootId, path: meta.path });
    await refreshSelected();
  }

  async function downloadEntry(meta: FileTreeNodeMeta): Promise<void> {
    if (meta.kind === FILE_ENTRY_KIND.file) {
      startBrowserDownload(fileDownloadUrl(meta.rootId, meta.path));
      return;
    }
    if (downloadActivity) return;

    downloadActivity = {
      fileName: `${meta.name}.zip`,
      message: "Collecting visible files…",
      detail: "The download will begin automatically when the archive is ready.",
      progressPercent: null,
    };

    try {
      const ready = await prepareFolderArchiveDownload(meta.rootId, meta.path, {
        onProgress: (preparation) => {
          const fileLabel = preparation.totalFiles === 1 ? "file" : "files";
          downloadActivity = {
            fileName: preparation.fileName,
            message: preparation.ready ? "Archive ready. Starting download…" : "Compressing folder…",
            detail: `${preparation.processedFiles.toLocaleString()} of ${preparation.totalFiles.toLocaleString()} ${fileLabel}`,
            progressPercent: preparation.progressPercent,
          };
        },
      });
      startBrowserDownload(fileArchiveDownloadUrl(ready.id));
      await new Promise((resolve) => window.setTimeout(resolve, 1_000));
    } finally {
      downloadActivity = null;
    }
  }

  function startBrowserDownload(url: string): void {
    const link = document.createElement("a");
    link.href = url;
    link.download = "";
    link.hidden = true;
    document.body.append(link);
    link.click();
    link.remove();
  }

  async function handleAction(action: FileActionId, treePath = selectedTreePath): Promise<void> {
    const meta = treePath ? registry.get(treePath) : null;
    if (!meta || !fileContextActions(meta.kind, meta.path === "", meta.excluded, meta.isReadOnly, canPickFolders).some((choice) => choice.id === action)) return;
    try {
      if (action === "open") await selectTreePath(meta.treePath);
      if (action === "download") await downloadEntry(meta);
      if (action === "new-folder") await createFolder(meta.kind === "directory" ? meta : directoryTarget(meta)!);
      if (action === "upload") requestUpload("files", meta);
      if (action === "upload-media") requestUpload("media", meta);
      if (action === "upload-folder") requestUpload("folder", meta);
      if (action === "rename") await renameFile(meta);
      if (action === "move") await moveFile(meta);
      if (action === "delete") await deleteFile(meta);
      if (action === "rescan") await rescan(meta);
      if (action === "exclude") await excludePath(meta);
      if (action === "remove-exclusion") await removeExclusion(meta);
    } catch (operationError) {
      const message = operationError instanceof Error ? operationError.message : "File operation failed";
      error = message.includes("already exists")
        ? `${message} Choose a different name and try again.`
        : message;
    }
  }

  interface PendingUpload {
    items: FileUploadItem[];
    target: FileTreeNodeMeta;
  }

  const CONFLICT_POLICY_STORAGE_KEY = "prismedia.files.upload-conflict-policy";
  const conflictPolicies = new Set<string>(Object.values(FILE_UPLOAD_CONFLICT_POLICY));
  let pendingUpload = $state<PendingUpload | null>(null);
  let conflictPolicy = $state<FileUploadConflictPolicyCode>(readConflictPolicy());
  let uploadSnapshot = $state<UploadQueueSnapshot | null>(null);
  let snapshotFrame = 0;
  const refreshTargets = new Map<string, FileTreeNodeMeta>();
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  const uploads = new UploadQueue({
    transport: createFileUploadTransport(),
    onChange: (snapshot) => {
      // Progress events arrive many times per second; render at most once per frame.
      if (snapshotFrame) cancelAnimationFrame(snapshotFrame);
      snapshotFrame = requestAnimationFrame(() => {
        snapshotFrame = 0;
        uploadSnapshot = snapshot;
      });
    },
    onFileStored: (item) => {
      const target = [...registry.values()].find((meta) =>
        meta.rootId === item.rootId && meta.path === item.targetPath && meta.kind === FILE_ENTRY_KIND.directory);
      if (target) scheduleFolderRefresh(target);
    },
  });

  function readConflictPolicy(): FileUploadConflictPolicyCode {
    try {
      const saved = localStorage.getItem(CONFLICT_POLICY_STORAGE_KEY);
      if (saved && conflictPolicies.has(saved)) return saved as FileUploadConflictPolicyCode;
    } catch {
      // Storage can be unavailable in private windows; the default still works.
    }
    return FILE_UPLOAD_CONFLICT_POLICY.keepBoth;
  }

  function setConflictPolicy(policy: FileUploadConflictPolicyCode): void {
    conflictPolicy = policy;
    try {
      localStorage.setItem(CONFLICT_POLICY_STORAGE_KEY, policy);
    } catch {
      // Remembering the choice is a convenience only.
    }
  }

  /** Reloads a target folder shortly after files land in it, batching many finished files. */
  function scheduleFolderRefresh(target: FileTreeNodeMeta): void {
    refreshTargets.set(target.treePath, target);
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      const targets = [...refreshTargets.values()];
      refreshTargets.clear();
      void (async () => {
        for (const folder of targets) {
          const nextLoaded = new Set(loadedKeys);
          nextLoaded.delete(loadedKey(folder));
          loadedKeys = nextLoaded;
          await loadChildren(folder);
        }
        if (selectedMeta && targets.some((folder) => folder.treePath === directoryTarget(selectedMeta)?.treePath)) {
          await loadDetail(selectedMeta);
        }
      })();
    }, 1_500);
  }

  /** Asks for the batch's conflict choice before any bytes are sent. */
  function uploadItems(items: FileUploadItem[], target = directoryTarget(selectedMeta)): void {
    if (!target || items.length === 0) return;
    if (target.isReadOnly) { error = "This library is managed by the connected application and is read-only."; return; }
    pendingUpload = { items, target };
  }

  function startPendingUpload(): void {
    const pending = pendingUpload;
    pendingUpload = null;
    if (!pending) return;
    uploads.add(pending.items, {
      rootId: pending.target.rootId,
      targetPath: pending.target.path,
      conflictPolicy,
    });
  }

  function warnBeforeLeaving(event: BeforeUnloadEvent): void {
    if (!uploads.snapshot().active) return;
    event.preventDefault();
  }

  type UploadPickerKind = "files" | "media" | "folder";

  // Hidden native pickers behind every upload button and menu action. The chosen folder is held
  // until the picker resolves because the native dialog is asynchronous. Picking from the tree
  // makes a folder the target without selecting it first, which matters on touch screens.
  const canPickFolders = supportsFolderPicker();
  let filesPicker = $state<HTMLInputElement | null>(null);
  let mediaPicker = $state<HTMLInputElement | null>(null);
  let folderPicker = $state<HTMLInputElement | null>(null);
  let pickerTarget: FileTreeNodeMeta | null = null;

  function requestUpload(kind: UploadPickerKind, meta: FileTreeNodeMeta): void {
    const target = directoryTarget(meta);
    if (!target) return;
    pickerTarget = target;
    const picker = kind === "media" ? mediaPicker : kind === "folder" ? folderPicker : filesPicker;
    picker?.click();
  }

  function onUploadPicked(input: HTMLInputElement): void {
    const target = pickerTarget;
    pickerTarget = null;
    const files = input.files;
    const items = fileListToUploadItems(files);
    input.value = "";
    if (target) uploadItems(items, target);
  }

  // ── Multi-select and bulk operations ─────────────────────────────
  let treePane = $state<{ setSelection: (treePaths: readonly string[]) => void } | null>(null);
  let selectionActive = $state(false);
  let selectedTreePaths = $state<string[]>([]);
  let bulkNotice = $state<{ tone: "success" | "warning" | "error"; message: string } | null>(null);
  let moveDialogOpen = $state(false);

  const selectedMetas = $derived(
    selectedTreePaths.map((treePath) => registry.get(treePath)).filter((meta): meta is FileTreeNodeMeta => Boolean(meta)),
  );
  const writableSelection = $derived(topLevelSelection(selectedMetas.filter((meta) => !meta.isReadOnly)));
  const moveTargets = $derived(
    [...registry.values()]
      .filter((meta) => meta.kind === FILE_ENTRY_KIND.directory && !meta.isReadOnly)
      .map((meta) => ({ value: meta.treePath, label: meta.treePath.replace(/\/$/, "") }))
      .sort((a, b) => a.label.localeCompare(b.label)),
  );
  const bulkActions: EntityGridBulkAction[] = [
    {
      id: "move",
      label: "Move to…",
      isAvailable: () => writableSelection.length > 0,
      onRun: () => (moveDialogOpen = true),
    },
    {
      id: "delete",
      label: "Delete permanently",
      tone: "danger",
      isAvailable: () => writableSelection.length > 0,
      onRun: () => confirmBulkDelete(),
    },
  ];

  function setSelectionActive(active: boolean): void {
    selectionActive = active;
    // Entering or leaving selection mode starts from an empty selection, so the folder that was
    // open is not silently part of the bulk action.
    treePane?.setSelection([]);
  }

  /** Library roots cannot be moved or deleted, so they never count toward a multi-selection. */
  function updateSelection(treePaths: string[]): void {
    selectedTreePaths = treePaths.filter((treePath) => registry.get(treePath)?.path !== "");
  }

  /** Selects every loaded entry in the folder of the current selection (or the selected folder). */
  function selectAllInFolder(): void {
    const anchor = selectedMetas[0] ?? selectedMeta;
    const folder = anchor?.kind === FILE_ENTRY_KIND.directory && selectedMetas.length === 0 ? anchor : directoryTarget(anchor ?? null);
    if (!folder) return;
    treePane?.setSelection([...registry.values()]
      .filter((meta) => meta.rootId === folder.rootId && meta.path !== "" && parentPath(meta.path) === folder.path)
      .map((meta) => meta.treePath));
  }

  async function finishBulk(notice: { tone: "success" | "warning" | "error"; message: string }): Promise<void> {
    bulkNotice = notice;
    treePane?.setSelection([]);
    selectionActive = false;
    selectedTreePath = null;
    detail = null;
    await loadRoots();
  }

  /** Moves several entries into one folder, one at a time, then reports what happened. */
  async function moveMany(
    sourceTreePaths: string[],
    targetTreePath: string | null,
    onProgress?: (done: number, total: number) => void,
  ): Promise<void> {
    const target = targetTreePath ? registry.get(targetTreePath) : null;
    if (!target || target.kind !== FILE_ENTRY_KIND.directory) return;
    if (target.isReadOnly) { error = "Externally managed library files cannot be moved here."; return; }
    const sources = sourceTreePaths.map((path) => registry.get(path)).filter((meta): meta is FileTreeNodeMeta => Boolean(meta));
    const readOnly = sources.filter((meta) => meta.isReadOnly).map((item) => ({ item, reason: "read-only library" }));
    const { moves, skipped } = planBulkMove(sources.filter((meta) => !meta.isReadOnly), target);
    const result = await runBulkOperation(
      moves,
      (move) => apiMoveFile({
        sourceRootId: move.item.rootId,
        sourcePath: move.item.path,
        targetRootId: move.targetRootId,
        targetPath: move.targetPath,
      }),
      onProgress,
    );
    await finishBulk(summarizeBulkResult(
      "Moved",
      { succeeded: result.succeeded.map((move) => move.item), failed: result.failed.map(({ item, message }) => ({ item: item.item, message })) },
      [...readOnly, ...skipped],
    ));
  }

  function confirmBulkDelete(): void {
    const items = writableSelection;
    if (items.length === 0) return;
    const folders = items.filter((meta) => meta.kind === FILE_ENTRY_KIND.directory).length;
    pendingConfirmation = {
      title: `Delete ${items.length.toLocaleString()} ${items.length === 1 ? "item" : "items"}?`,
      message: folders
        ? `This permanently deletes ${items.length.toLocaleString()} selected items, including ${folders.toLocaleString()} ${folders === 1 ? "folder" : "folders"} and everything inside them.`
        : `This permanently deletes ${items.length.toLocaleString()} selected files.`,
      confirmLabel: "Delete permanently",
      danger: true,
      onConfirm: async () => {
        const result = await runBulkOperation(items, (meta) => apiDeleteFile(meta.rootId, meta.path));
        await finishBulk(summarizeBulkResult("Deleted", result));
      },
    };
  }

  async function handleExternalDrop(dataTransfer: DataTransfer | null): Promise<void> {
    const target = directoryTarget(selectedMeta);
    uploadItems(await collectDroppedFiles(dataTransfer), target ?? undefined);
  }

  $effect(() => {
    if (roots.length === 0) return;
    const rootId = page.url.searchParams.get("rootId");
    const path = page.url.searchParams.get("path") ?? "";
    const queryKey = `${rootId}:${path}`;
    if (!rootId || queryKey === syncedQueryKey) return;
    syncedQueryKey = queryKey;
    const root = roots.find((candidate) => candidate.id === rootId);
    if (!root) return;
    const rootTreePath = `${fileTreeRootPath(root, roots)}/`;
    void (async () => {
      const rootMeta = registry.get(rootTreePath);
      if (!rootMeta) return;
      if (path && !registry.has(`${rootTreePath}${path}`)) await loadChildren(rootMeta);
      await selectTreePath(path ? `${rootTreePath}${path}` : rootTreePath, { replaceUrl: true, showDetail: false });
    })();
  });

  function onResizeStart(event: PointerEvent): void {
    event.preventDefault();
    resizing = true;
    const startX = event.clientX;
    const startWidth = treeWidthPx;

    function onMove(moveEvent: PointerEvent): void {
      const delta = moveEvent.clientX - startX;
      treeWidthPx = Math.max(200, Math.min(startWidth + delta, window.innerWidth * 0.6));
    }

    function onUp(): void {
      resizing = false;
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
    }

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
  }

  onMount(() => {
    lastNsfwMode = nsfw.mode;
    void loadRoots();
    window.addEventListener("beforeunload", warnBeforeLeaving);
    return () => window.removeEventListener("beforeunload", warnBeforeLeaving);
  });

  onDestroy(() => {
    uploads.cancelAll();
    if (refreshTimer) clearTimeout(refreshTimer);
    if (snapshotFrame) cancelAnimationFrame(snapshotFrame);
  });

  $effect(() => {
    if (lastNsfwMode === null || nsfw.mode === lastNsfwMode) return;
    lastNsfwMode = nsfw.mode;
    selectedTreePath = null;
    detail = null;
    registry = new Map();
    treePaths = [];
    loadedKeys = new Set();
    syncedQueryKey = "";
    void loadRoots();
  });
</script>

<svelte:head>
  <title>Files · Prismedia</title>
</svelte:head>

<main
  class:show-detail={mobileDetail}
  class:resizing
  class="files-page"
  style:--tree-width="{treeWidthPx}px"
>
  <FileTreePane
    paths={treePaths}
    {registry}
    {loadedKeys}
    selectedPath={selectedTreePath}
    search={treeSearch}
    loading={loadingTree}
    onSearch={(value) => (treeSearch = value)}
    onSelect={(treePath) => void selectTreePath(treePath)}
    onExpand={(meta) => void loadChildren(meta)}
    onMove={(source, target) => void moveFile(registry.get(source)!, target)}
    onRename={(treePath, newName) => void renameFile(registry.get(treePath)!, newName)}
    onAction={(action, treePath) => void handleAction(action, treePath)}
    {canPickFolders}
    bind:this={treePane}
    {selectionActive}
    selectedCount={selectedTreePaths.length}
    selectedPaths={selectedTreePaths}
    {bulkActions}
    notice={bulkNotice}
    onSelectionChange={updateSelection}
    onSelectionActiveChange={setSelectionActive}
    onSelectAllVisible={selectAllInFolder}
    onMoveMany={(sources, target) => void moveMany(sources, target)}
    onDismissNotice={() => (bulkNotice = null)}
  />

  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="resize-handle" onpointerdown={onResizeStart}>
    <div class="resize-handle-bar"></div>
  </div>

  <FileDetailPane
    {detail}
    isReadOnly={Boolean(selectedMeta?.isReadOnly)}
    loading={loadingDetail}
    {error}
    mobile={mobileDetail}
    downloadBusy={downloadActivity !== null}
    onBack={() => (mobileDetail = false)}
    onRefresh={() => void refreshSelected()}
    onAction={(action) => void handleAction(action)}
    {canPickFolders}
    onExternalDrop={(dataTransfer) => void handleExternalDrop(dataTransfer)}
  />

  <input
    bind:this={filesPicker}
    type="file"
    multiple
    class="hidden-input"
    tabindex="-1"
    aria-hidden="true"
    onchange={(event) => onUploadPicked(event.currentTarget)}
  />
  <input
    bind:this={mediaPicker}
    type="file"
    multiple
    accept={MEDIA_PICKER_ACCEPT}
    class="hidden-input"
    tabindex="-1"
    aria-hidden="true"
    onchange={(event) => onUploadPicked(event.currentTarget)}
  />
  {#if canPickFolders}
    <input
      bind:this={folderPicker}
      type="file"
      multiple
      webkitdirectory
      class="hidden-input"
      tabindex="-1"
      aria-hidden="true"
      onchange={(event) => onUploadPicked(event.currentTarget)}
    />
  {/if}
</main>

{#if uploadSnapshot && uploadSnapshot.items.length > 0}
  <FileUploadPanel
    snapshot={uploadSnapshot}
    onCancel={(id) => uploads.cancel(id)}
    onCancelAll={() => uploads.cancelAll()}
    onRetry={(id) => uploads.retry(id)}
    onRetryFailed={() => uploads.retryFailed()}
    onClearFinished={() => uploads.clearFinished()}
    onDismiss={() => {
      uploads.cancelAll();
      uploads.clearFinished();
    }}
    raised={downloadActivity !== null}
  />
{/if}

<FileMoveDialog
  open={moveDialogOpen}
  itemCount={writableSelection.length}
  folders={moveTargets}
  onConfirm={(target, onProgress) => moveMany(writableSelection.map((meta) => meta.treePath), target, onProgress)}
  onClose={() => (moveDialogOpen = false)}
/>

<FileUploadDialog
  open={pendingUpload !== null}
  fileCount={pendingUpload?.items.length ?? 0}
  totalBytes={pendingUpload?.items.reduce((sum, item) => sum + item.file.size, 0) ?? 0}
  destination={pendingUpload ? `${pendingUpload.target.name}${pendingUpload.target.path ? ` (/${pendingUpload.target.path})` : ""}` : ""}
  {conflictPolicy}
  onConflictPolicyChange={setConflictPolicy}
  onConfirm={startPendingUpload}
  onClose={() => (pendingUpload = null)}
/>

{#if downloadActivity}
  <FileDownloadStatus
    fileName={downloadActivity.fileName}
    message={downloadActivity.message}
    detail={downloadActivity.detail}
    progressPercent={downloadActivity.progressPercent}
  />
{/if}

<NameInputDialog
  open={pendingNameDialog !== null}
  title={pendingNameDialog?.title ?? "Name"}
  initialValue={pendingNameDialog?.initialValue ?? ""}
  confirmLabel={pendingNameDialog?.confirmLabel ?? "Save"}
  onConfirm={async (value) => {
    await pendingNameDialog?.onConfirm(value);
  }}
  onClose={() => (pendingNameDialog = null)}
/>

<ConfirmDialog
  open={pendingConfirmation !== null}
  title={pendingConfirmation?.title ?? "Confirm"}
  message={pendingConfirmation?.message ?? ""}
  confirmLabel={pendingConfirmation?.confirmLabel ?? "Confirm"}
  danger={pendingConfirmation?.danger ?? false}
  onConfirm={async () => {
    await pendingConfirmation?.onConfirm();
  }}
  onClose={() => (pendingConfirmation = null)}
/>

<style>
  .hidden-input {
    display: none;
  }

  .files-page {
    display: grid;
    width: calc(100% + 2.5rem);
    max-width: calc(100% + 2.5rem);
    min-width: 0;
    min-height: 0;
    overflow: hidden;
    background: var(--color-bg);
    margin: -1.25rem;
    height: calc(100% + 2.5rem);
  }

  .files-page :global(.detail-pane) {
    display: none;
  }

  .files-page.show-detail :global(.files-tree-pane) {
    display: none;
  }

  .files-page.show-detail :global(.detail-pane) {
    display: grid;
  }

  .resize-handle {
    display: none;
  }

  .files-page.resizing {
    user-select: none;
    cursor: col-resize;
  }

  @media (min-width: 768px) {
    .files-page {
      grid-template-columns: var(--tree-width, 320px) auto minmax(0, 1fr);
    }

    .files-page :global(.detail-pane),
    .files-page.show-detail :global(.detail-pane) {
      display: grid;
    }

    .files-page.show-detail :global(.files-tree-pane) {
      display: flex;
    }

    .resize-handle {
      display: flex;
      align-items: center;
      justify-content: center;
      width: 5px;
      cursor: col-resize;
      background: var(--color-border-subtle);
      transition: background var(--duration-fast) var(--ease-default);
      touch-action: none;
    }

    .resize-handle:hover,
    .resizing .resize-handle {
      background: var(--color-border-accent);
    }

    .resize-handle-bar {
      width: 1px;
      height: 100%;
      background: transparent;
    }
  }
</style>
