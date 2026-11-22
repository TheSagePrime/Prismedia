/**
 * Collects files from drag-and-drop and file pickers into upload items that keep each file's
 * path relative to the drop, so dropped folders are recreated on the server.
 */

/** One file to upload and its path relative to the chosen target folder. */
export interface FileUploadItem {
  file: File;
  relativePath: string;
}

/** Minimal shape of the browser's File and Directory Entries API used by drops. */
export interface DroppedEntry {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
}

interface DroppedFileEntry extends DroppedEntry {
  file: (success: (file: File) => void, error?: (error: DOMException) => void) => void;
}

/** Reader that returns a directory's children in batches; an empty batch means it is finished. */
export interface DroppedDirectoryReader {
  readEntries: (success: (entries: DroppedEntry[]) => void, error?: (error: DOMException) => void) => void;
}

interface DroppedDirectoryEntry extends DroppedEntry {
  createReader: () => DroppedDirectoryReader;
}

/**
 * Reads every child of a dropped directory. Browsers return entries in batches (Chromium stops at
 * 100 per call), so the reader is called until it returns an empty batch; a single call would
 * silently upload only part of a large folder.
 */
export async function readAllDirectoryEntries(reader: DroppedDirectoryReader): Promise<DroppedEntry[]> {
  const all: DroppedEntry[] = [];
  for (;;) {
    const batch = await new Promise<DroppedEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
    if (batch.length === 0) return all;
    all.push(...batch);
  }
}

/** Recursively expands a dropped entry into upload items rooted at `basePath`. */
export async function traverseDroppedEntry(entry: DroppedEntry, basePath = ""): Promise<FileUploadItem[]> {
  const relativePath = [basePath, entry.name].filter(Boolean).join("/");
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) => {
      (entry as DroppedFileEntry).file(resolve, reject);
    });
    return [{ file, relativePath }];
  }
  if (!entry.isDirectory) return [];
  const children = await readAllDirectoryEntries((entry as DroppedDirectoryEntry).createReader());
  const nested: FileUploadItem[] = [];
  // Sequential traversal keeps memory and open handles bounded for very large folders.
  for (const child of children) nested.push(...(await traverseDroppedEntry(child, relativePath)));
  return nested;
}

/**
 * Turns a drop into upload items. Folder entries are expanded when the browser exposes them;
 * otherwise the plain file list is used.
 */
export async function collectDroppedFiles(dataTransfer: DataTransfer | null): Promise<FileUploadItem[]> {
  if (!dataTransfer) return [];
  // Entries must be taken synchronously, before any await, or the browser clears the drop data.
  const sources = [...dataTransfer.items]
    .filter((item) => item.kind === "file")
    .map((item) => ({
      entry: "webkitGetAsEntry" in item
        ? (item as DataTransferItem & { webkitGetAsEntry: () => DroppedEntry | null }).webkitGetAsEntry()
        : null,
      file: item.getAsFile(),
    }));
  const collected: FileUploadItem[] = [];
  for (const source of sources) {
    if (source.entry) collected.push(...(await traverseDroppedEntry(source.entry)));
    else if (source.file) collected.push({ file: source.file, relativePath: source.file.name });
  }
  return collected.length
    ? collected
    : Array.from(dataTransfer.files).map((file) => ({ file, relativePath: file.name }));
}

/** Converts a picker's file list, keeping folder structure when a folder was picked. */
export function fileListToUploadItems(files: FileList | readonly File[] | null): FileUploadItem[] {
  if (!files) return [];
  return Array.from(files).map((file) => ({
    file,
    relativePath: (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name,
  }));
}
