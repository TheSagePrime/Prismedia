import {
  cancelFileUpload,
  completeFileUpload,
  createFileFolder as createFileFolderRequest,
  createFileUpload,
  deleteFile as deleteFileRequest,
  getDownloadFileArchiveUrl,
  getDownloadFileUrl,
  excludeFile as excludeFileRequest,
  getFileArchiveStatus,
  getFileDetail,
  getFileUpload,
  getGetFileContentUrl,
  getUploadFileChunkUrl,
  listFileChildren,
  listFileRoots,
  moveFile as moveFileRequest,
  prepareFileArchive,
  removeFileExclusion as removeFileExclusionRequest,
  renameFile as renameFileRequest,
  rescanFileRoot as rescanFileRootRequest,
} from "$lib/api/generated/prismedia";
import type {
  FileChildrenResponse,
  FileArchivePreparation as GeneratedFileArchivePreparation,
  FileCreateFolderRequest,
  FileDetail as GeneratedFileDetail,
  FileEntry,
  FileExclusionRequest,
  FileMoveRequest,
  FileOperationResponse as GeneratedFileOperationResponse,
  FileRenameRequest,
  FileRescanRequest,
  FileRoot,
  FileRootsResponse,
  FileUploadCompletion,
  FileUploadSession,
} from "$lib/api/generated/model";
import { requestInit, unwrapGenerated, type RequestOptions } from "$lib/api/generated-response";
import { ApiError, apiPath } from "$lib/api/orval-fetch";
import {
  UploadTransportError,
  type UploadCompletionResult,
  type UploadSessionProgress,
  type UploadTransport,
} from "$lib/files/upload-queue";
import type { EntityFileRoleCode } from "$lib/entities/entity-codes";

export type { FileChildrenResponse, FileEntry, FileRoot, FileRootsResponse };
export type FileDetail = Omit<
  GeneratedFileDetail,
  "directoryFileCount" | "directoryTotalSizeBytes"
> & {
  directoryFileCount?: number | null;
  directoryTotalSizeBytes?: number | null;
};

export interface FileOperationResponse {
  scansQueued: number;
}

export type { FileUploadItem } from "$lib/files/dropped-files";

export interface FileArchivePreparation {
  id: string;
  fileName: string;
  ready: boolean;
  progressPercent: number;
  processedFiles: number;
  totalFiles: number;
  error: string | null;
}

export interface FileArchivePreparationOptions extends RequestOptions {
  pollIntervalMs?: number;
  onProgress?: (preparation: FileArchivePreparation) => void;
}

export async function fetchFileRoots(options?: RequestOptions): Promise<FileRootsResponse> {
  return unwrapGenerated(
    await listFileRoots(undefined, requestInit(options)),
    "Failed to load file roots",
  );
}

export async function fetchFileChildren(
  rootId: string,
  path = "",
  options?: RequestOptions,
): Promise<FileChildrenResponse> {
  return unwrapGenerated(
    await listFileChildren({ rootId, ...(path ? { path } : {}) }, requestInit(options)),
    "Failed to load folder",
  );
}

export async function fetchFileDetail(
  rootId: string,
  path = "",
  options?: RequestOptions,
): Promise<FileDetail> {
  return normalizeFileDetail(
    unwrapGenerated(
      await getFileDetail({ rootId, ...(path ? { path } : {}) }, requestInit(options)),
      "Failed to load file details",
    ),
  );
}

export function fileContentUrl(rootId: string, path = ""): string {
  return apiPath(getGetFileContentUrl({ rootId, path }));
}

export function fileDownloadUrl(rootId: string, path = ""): string {
  return apiPath(getDownloadFileUrl({ rootId, path }));
}

export function fileArchiveDownloadUrl(id: string): string {
  return apiPath(getDownloadFileArchiveUrl(id));
}

export async function prepareFolderArchiveDownload(
  rootId: string,
  path = "",
  options: FileArchivePreparationOptions = {},
): Promise<FileArchivePreparation> {
  let preparation = normalizeArchivePreparation(
    unwrapGenerated(
      await prepareFileArchive(
        { rootId, path },
        undefined,
        requestInit(options),
      ),
      "Failed to start folder archive",
      [202],
    ),
  );
  options.onProgress?.(preparation);

  while (!preparation.ready && preparation.error === null) {
    await waitForPoll(options.pollIntervalMs ?? 500, options.signal);
    preparation = normalizeArchivePreparation(
      unwrapGenerated(
        await getFileArchiveStatus(preparation.id, requestInit(options)),
        "Failed to check folder archive",
      ),
    );
    options.onProgress?.(preparation);
  }

  if (preparation.error) throw new Error(preparation.error);
  return preparation;
}

export function entityFileUrl(entityId: string, role: EntityFileRoleCode): string {
  return apiPath(`/entities/${encodeURIComponent(entityId)}/files/${encodeURIComponent(role)}`);
}

export async function createFileFolder(
  payload: FileCreateFolderRequest,
  options?: RequestOptions,
): Promise<FileOperationResponse> {
  return normalizeFileOperation(
    unwrapGenerated(
      await createFileFolderRequest(payload, undefined, requestInit(options)),
      "Failed to create folder",
    ),
  );
}

/**
 * Resumable upload transport over the Files upload API. Chunks are sent with XMLHttpRequest so
 * the queue can show byte-level progress; every failure is reported as an `UploadTransportError`
 * with its HTTP status (0 when the connection dropped) and problem code.
 */
export function createFileUploadTransport(): UploadTransport {
  return {
    async create(request, signal) {
      const response = await translateUploadErrors(() => createFileUpload(request, undefined, { signal }));
      const data = response.data;
      return {
        session: data.session ? normalizeUploadSession(data.session) : null,
        completion: data.completion ? normalizeUploadCompletion(data.completion) : null,
      };
    },
    async status(id, signal) {
      const response = await translateUploadErrors(() => getFileUpload(id, { signal }));
      return normalizeUploadSession(response.data);
    },
    sendChunk(id, offset, chunk, onProgress, signal) {
      return sendUploadChunk(apiPath(getUploadFileChunkUrl(id, { offset })), chunk, onProgress, signal);
    },
    async complete(id, signal) {
      const response = await translateUploadErrors(() => completeFileUpload(id, undefined, { signal }));
      return normalizeUploadCompletion(response.data);
    },
    async cancel(id) {
      await translateUploadErrors(() => cancelFileUpload(id));
    },
  };
}

async function translateUploadErrors<T>(request: () => Promise<T>): Promise<T> {
  try {
    return await request();
  } catch (error) {
    if (error instanceof ApiError) throw new UploadTransportError(error.message, error.status, error.problemCode);
    if (error instanceof DOMException && error.name === "AbortError") throw error;
    throw new UploadTransportError("The connection to the server was lost.", 0);
  }
}

function sendUploadChunk(
  url: string,
  chunk: Blob,
  onProgress: (loadedBytes: number) => void,
  signal: AbortSignal,
): Promise<UploadSessionProgress> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    const onAbort = () => request.abort();
    request.open("PUT", url);
    request.withCredentials = true;
    request.setRequestHeader("Content-Type", "application/octet-stream");
    request.upload.onprogress = (event) => onProgress(event.loaded);
    request.onload = () => {
      signal.removeEventListener("abort", onAbort);
      let data: unknown = null;
      try {
        data = request.responseText ? JSON.parse(request.responseText) : null;
      } catch {
        data = null;
      }
      if (request.status >= 200 && request.status < 300) {
        resolve(normalizeUploadSession(data as FileUploadSession));
        return;
      }
      const problem = (data ?? {}) as { message?: string; code?: string };
      reject(new UploadTransportError(problem.message ?? `Upload failed (${request.status})`, request.status, problem.code));
    };
    request.onerror = () => {
      signal.removeEventListener("abort", onAbort);
      reject(new UploadTransportError("The connection to the server was lost.", 0));
    };
    request.ontimeout = request.onerror;
    request.onabort = () => {
      signal.removeEventListener("abort", onAbort);
      reject(signal.reason ?? new DOMException("Upload cancelled.", "AbortError"));
    };
    if (signal.aborted) {
      reject(signal.reason);
      return;
    }
    signal.addEventListener("abort", onAbort, { once: true });
    request.send(chunk);
  });
}

function normalizeUploadSession(session: FileUploadSession): UploadSessionProgress {
  return {
    id: session.id,
    receivedBytes: Number(session.receivedBytes),
    maxChunkBytes: Number(session.maxChunkBytes),
  };
}

function normalizeUploadCompletion(completion: FileUploadCompletion): UploadCompletionResult {
  return { outcome: completion.outcome, relativePath: completion.relativePath };
}

export async function renameFile(
  payload: FileRenameRequest,
  options?: RequestOptions,
): Promise<FileOperationResponse> {
  return normalizeFileOperation(
    unwrapGenerated(
      await renameFileRequest(payload, undefined, requestInit(options)),
      "Failed to rename file",
    ),
  );
}

export async function moveFile(
  payload: FileMoveRequest,
  options?: RequestOptions,
): Promise<FileOperationResponse> {
  return normalizeFileOperation(
    unwrapGenerated(
      await moveFileRequest(payload, undefined, requestInit(options)),
      "Failed to move file",
    ),
  );
}

export async function deleteFile(
  rootId: string,
  path: string,
  options?: RequestOptions,
): Promise<FileOperationResponse> {
  return normalizeFileOperation(
    unwrapGenerated(
      await deleteFileRequest({ rootId, path }, requestInit(options)),
      "Failed to delete file",
    ),
  );
}

export async function excludeFile(
  payload: FileExclusionRequest,
  options?: RequestOptions,
): Promise<FileOperationResponse> {
  return normalizeFileOperation(
    unwrapGenerated(
      await excludeFileRequest(payload, undefined, requestInit(options)),
      "Failed to exclude file",
    ),
  );
}

export async function removeFileExclusion(
  payload: FileExclusionRequest,
  options?: RequestOptions,
): Promise<FileOperationResponse> {
  return normalizeFileOperation(
    unwrapGenerated(
      await removeFileExclusionRequest(payload, requestInit(options)),
      "Failed to remove file exclusion",
    ),
  );
}

export async function rescanFileRoot(
  payload: FileRescanRequest,
  options?: RequestOptions,
): Promise<FileOperationResponse> {
  return normalizeFileOperation(
    unwrapGenerated(
      await rescanFileRootRequest(payload, undefined, requestInit(options)),
      "Failed to queue file rescan",
    ),
  );
}

function normalizeFileDetail(detail: GeneratedFileDetail): FileDetail {
  return {
    ...detail,
    directoryFileCount: normalizeOptionalNumber(detail.directoryFileCount),
    directoryTotalSizeBytes: normalizeOptionalNumber(detail.directoryTotalSizeBytes),
  };
}

function normalizeFileOperation(
  response: GeneratedFileOperationResponse,
): FileOperationResponse {
  return {
    scansQueued: Number(response.scansQueued),
  };
}

function normalizeArchivePreparation(
  preparation: GeneratedFileArchivePreparation,
): FileArchivePreparation {
  return {
    ...preparation,
    progressPercent: Number(preparation.progressPercent),
    processedFiles: Number(preparation.processedFiles),
    totalFiles: Number(preparation.totalFiles),
  };
}

function waitForPoll(delayMs: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.reject(signal.reason);
  return new Promise((resolve, reject) => {
    const onElapsed = () => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    };
    const onAbort = () => {
      window.clearTimeout(timeout);
      reject(signal?.reason ?? new DOMException("Archive preparation was cancelled.", "AbortError"));
    };
    const timeout = window.setTimeout(onElapsed, delayMs);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

function normalizeOptionalNumber(value: number | string | null | undefined): number | null {
  if (value == null || value === "") return null;
  return Number(value);
}
