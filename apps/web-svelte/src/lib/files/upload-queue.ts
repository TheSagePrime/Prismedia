/**
 * Client-side queue for resumable Files uploads. Every file is its own server upload session:
 * it is sent in ordered chunks, failed chunks are retried with exponential backoff, and a file
 * that still fails keeps its session so "Retry" resumes from the bytes the server already holds.
 * One file failing never stops the rest of the batch.
 *
 * The queue is framework-free; the Files page subscribes through `onChange` and renders snapshots.
 */
import {
  FILE_UPLOAD_OUTCOME,
  PROBLEM_CODE,
  type FileUploadConflictPolicyCode,
  type FileUploadOutcomeCode,
} from "$lib/api/generated/codes";
import type { FileUploadItem } from "./dropped-files";

/** Default chunk size: small enough that a VPN blip costs little, large enough to keep requests few. */
export const DEFAULT_UPLOAD_CHUNK_BYTES = 8 * 1024 * 1024;

/** Failure reported by an upload transport, carrying the HTTP status (0 for network failures). */
export class UploadTransportError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly problemCode?: string,
  ) {
    super(message);
    this.name = "UploadTransportError";
  }
}

/** Final server result of one upload. */
export interface UploadCompletionResult {
  outcome: FileUploadOutcomeCode;
  relativePath: string;
}

/** Server progress of one upload session. */
export interface UploadSessionProgress {
  id: string;
  receivedBytes: number;
  maxChunkBytes: number;
}

/** HTTP operations the queue needs; implemented over the generated client in `$lib/api/files`. */
export interface UploadTransport {
  create(
    request: {
      rootId: string;
      targetPath: string;
      relativePath: string;
      sizeBytes: number;
      conflictPolicy: FileUploadConflictPolicyCode;
    },
    signal: AbortSignal,
  ): Promise<{ session: UploadSessionProgress | null; completion: UploadCompletionResult | null }>;
  status(id: string, signal: AbortSignal): Promise<UploadSessionProgress>;
  sendChunk(
    id: string,
    offset: number,
    chunk: Blob,
    onProgress: (loadedBytes: number) => void,
    signal: AbortSignal,
  ): Promise<UploadSessionProgress>;
  complete(id: string, signal: AbortSignal): Promise<UploadCompletionResult>;
  cancel(id: string): Promise<void>;
}

/** Where one queued file is in its lifecycle. UI-only state, not a server code. */
export type UploadItemStatus =
  | "queued"
  | "uploading"
  | "retrying"
  | "completing"
  | "done"
  | "failed"
  | "cancelled";

/** Read-only view of one queued file. */
export interface UploadItemSnapshot {
  id: string;
  name: string;
  relativePath: string;
  rootId: string;
  targetPath: string;
  sizeBytes: number;
  uploadedBytes: number;
  status: UploadItemStatus;
  /** Server outcome once done: uploaded, renamed, or skipped. */
  outcome: FileUploadOutcomeCode | null;
  /** Root-relative path the file ended up at (or the existing path when skipped). */
  finalPath: string | null;
  error: string | null;
  /** Retry attempts used for the current chunk. */
  attempt: number;
  /** When the next automatic retry starts, while retrying. */
  retryAt: number | null;
}

/** Totals across every file in the queue. */
export interface UploadQueueSnapshot {
  items: UploadItemSnapshot[];
  totalBytes: number;
  uploadedBytes: number;
  counts: Record<UploadItemStatus, number> & { renamed: number; skipped: number };
  /** Smoothed throughput over the last few seconds. */
  bytesPerSecond: number;
  /** Estimated seconds until all active and queued files finish, when known. */
  etaSeconds: number | null;
  active: boolean;
}

/** Options for one batch added to the queue. */
export interface UploadBatchOptions {
  rootId: string;
  targetPath: string;
  conflictPolicy: FileUploadConflictPolicyCode;
}

export interface UploadQueueOptions {
  transport: UploadTransport;
  chunkBytes?: number;
  /** Files uploading at the same time. */
  concurrency?: number;
  /** Automatic retries per chunk before a file is marked failed. */
  maxRetries?: number;
  /** Delay before retry number `attempt` (1-based). */
  backoffMs?: (attempt: number) => number;
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
  now?: () => number;
  onChange?: (snapshot: UploadQueueSnapshot) => void;
  /** Called after a file is stored in the library (not for skips or failures). */
  onFileStored?: (item: UploadItemSnapshot) => void;
}

interface QueueItem extends UploadItemSnapshot {
  file: File;
  conflictPolicy: FileUploadConflictPolicyCode;
  sessionId: string | null;
  /** Largest chunk the server accepts for this file's session. */
  chunkLimit: number;
  controller: AbortController | null;
}

const SPEED_WINDOW_MS = 5_000;

/** Exponential backoff with jitter, capped at 30 seconds. */
export function defaultBackoffMs(attempt: number): number {
  const base = Math.min(30_000, 1_000 * 2 ** (attempt - 1));
  return base + Math.floor(Math.random() * 250);
}

/**
 * Whether a failure is worth retrying automatically: dropped connections, timeouts, rate limits,
 * and server errors. Conflicts, invalid paths, a full disk, and missing sessions are not.
 */
export function isRetryableUploadError(error: unknown): boolean {
  if (!(error instanceof UploadTransportError)) return false;
  if (error.status === 0 || error.status === 408 || error.status === 429) return true;
  return error.status >= 500 && error.status !== 507;
}

function abortableSleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(signal.reason);
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal.reason);
    };
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message ? error.message : "Upload failed";
}

let nextItemId = 0;

/** Resumable, retrying upload queue for the Files page. */
export class UploadQueue {
  private readonly items: QueueItem[] = [];
  private readonly samples: { at: number; bytes: number }[] = [];
  private readonly transport: UploadTransport;
  private readonly chunkBytes: number;
  private readonly concurrency: number;
  private readonly maxRetries: number;
  private readonly backoffMs: (attempt: number) => number;
  private readonly sleep: (ms: number, signal: AbortSignal) => Promise<void>;
  private readonly now: () => number;
  private running = 0;
  private transferred = 0;

  constructor(private readonly options: UploadQueueOptions) {
    this.transport = options.transport;
    this.chunkBytes = options.chunkBytes ?? DEFAULT_UPLOAD_CHUNK_BYTES;
    this.concurrency = Math.max(1, options.concurrency ?? 2);
    this.maxRetries = options.maxRetries ?? 6;
    this.backoffMs = options.backoffMs ?? defaultBackoffMs;
    this.sleep = options.sleep ?? abortableSleep;
    this.now = options.now ?? (() => Date.now());
  }

  /** Adds files to the queue and starts uploading. Returns the ids of the new queue items. */
  add(files: FileUploadItem[], batch: UploadBatchOptions): string[] {
    const ids: string[] = [];
    for (const { file, relativePath } of files) {
      const id = `upload-${++nextItemId}`;
      ids.push(id);
      this.items.push({
        id,
        name: relativePath.split("/").at(-1) ?? file.name,
        relativePath,
        rootId: batch.rootId,
        targetPath: batch.targetPath,
        sizeBytes: file.size,
        uploadedBytes: 0,
        status: "queued",
        outcome: null,
        finalPath: null,
        error: null,
        attempt: 0,
        retryAt: null,
        file,
        conflictPolicy: batch.conflictPolicy,
        sessionId: null,
        chunkLimit: Number.POSITIVE_INFINITY,
        controller: null,
      });
    }
    this.emit();
    this.pump();
    return ids;
  }

  /** Stops one file and discards its partial bytes on the server. */
  cancel(id: string): void {
    const item = this.find(id);
    if (!item || item.status === "done" || item.status === "cancelled") return;
    item.controller?.abort(new DOMException("Upload cancelled.", "AbortError"));
    this.discardSession(item);
    item.status = "cancelled";
    item.retryAt = null;
    this.emit();
  }

  /** Stops every file that has not finished. */
  cancelAll(): void {
    for (const item of this.items) this.cancel(item.id);
  }

  /** Puts a failed or cancelled file back in the queue; it resumes from stored bytes when it can. */
  retry(id: string): void {
    const item = this.find(id);
    if (!item || (item.status !== "failed" && item.status !== "cancelled")) return;
    item.status = "queued";
    item.error = null;
    item.attempt = 0;
    this.emit();
    this.pump();
  }

  /** Retries every failed file. */
  retryFailed(): void {
    for (const item of this.items) if (item.status === "failed") this.retry(item.id);
  }

  /** Removes finished, skipped, and cancelled files from the list. */
  clearFinished(): void {
    for (let index = this.items.length - 1; index >= 0; index -= 1) {
      if (this.items[index].status === "done" || this.items[index].status === "cancelled") {
        this.items.splice(index, 1);
      }
    }
    this.emit();
  }

  /** Current state of the queue. */
  snapshot(): UploadQueueSnapshot {
    const counts = {
      queued: 0, uploading: 0, retrying: 0, completing: 0, done: 0, failed: 0, cancelled: 0,
      renamed: 0, skipped: 0,
    };
    let totalBytes = 0;
    let uploadedBytes = 0;
    let remainingBytes = 0;
    for (const item of this.items) {
      counts[item.status] += 1;
      if (item.outcome === FILE_UPLOAD_OUTCOME.renamed) counts.renamed += 1;
      if (item.outcome === FILE_UPLOAD_OUTCOME.skipped) counts.skipped += 1;
      if (item.status === "cancelled") continue;
      totalBytes += item.sizeBytes;
      uploadedBytes += item.status === "done" ? item.sizeBytes : item.uploadedBytes;
      if (item.status !== "done" && item.status !== "failed") remainingBytes += item.sizeBytes - item.uploadedBytes;
    }
    const active = counts.queued + counts.uploading + counts.retrying + counts.completing > 0;
    const bytesPerSecond = active ? this.speed() : 0;
    return {
      items: this.items.map(({ file: _file, controller: _controller, sessionId: _session, conflictPolicy: _policy, chunkLimit: _limit, ...rest }) => ({ ...rest })),
      totalBytes,
      uploadedBytes,
      counts,
      bytesPerSecond,
      etaSeconds: active && bytesPerSecond > 0 ? Math.ceil(remainingBytes / bytesPerSecond) : null,
      active,
    };
  }

  private find(id: string): QueueItem | undefined {
    return this.items.find((item) => item.id === id);
  }

  private emit(): void {
    this.options.onChange?.(this.snapshot());
  }

  private pump(): void {
    while (this.running < this.concurrency) {
      const next = this.items.find((item) => item.status === "queued");
      if (!next) return;
      this.running += 1;
      void this.run(next).finally(() => {
        this.running -= 1;
        this.pump();
      });
    }
  }

  private recordProgress(bytes: number): void {
    if (bytes <= 0) return;
    this.transferred += bytes;
    const at = this.now();
    this.samples.push({ at, bytes: this.transferred });
    while (this.samples.length > 2 && at - this.samples[0].at > SPEED_WINDOW_MS) this.samples.shift();
  }

  private speed(): number {
    if (this.samples.length < 2) return 0;
    const first = this.samples[0];
    const last = this.samples.at(-1)!;
    if (this.now() - last.at > SPEED_WINDOW_MS) return 0;
    const seconds = (last.at - first.at) / 1000;
    return seconds > 0 ? (last.bytes - first.bytes) / seconds : 0;
  }

  private discardSession(item: QueueItem): void {
    const sessionId = item.sessionId;
    item.sessionId = null;
    item.uploadedBytes = 0;
    if (sessionId) void this.transport.cancel(sessionId).catch(() => undefined);
  }

  private async run(item: QueueItem): Promise<void> {
    const controller = new AbortController();
    item.controller = controller;
    item.status = "uploading";
    item.error = null;
    this.emit();
    try {
      const started = await this.withRetries(item, controller.signal, () => this.ensureSession(item, controller.signal));
      if (started === "finished") return;
      while (item.uploadedBytes < item.sizeBytes) {
        await this.withRetries(item, controller.signal, () => this.sendNextChunk(item, controller.signal));
      }
      item.status = "completing";
      this.emit();
      const completion = await this.withRetries(item, controller.signal, () =>
        this.transport.complete(item.sessionId!, controller.signal));
      this.finish(item, completion);
    } catch (error) {
      if (controller.signal.aborted) return;
      item.status = "failed";
      item.retryAt = null;
      item.error = errorMessage(error);
      if (error instanceof UploadTransportError && error.status === 404) item.sessionId = null;
      this.emit();
    } finally {
      item.controller = null;
    }
  }

  private finish(item: QueueItem, completion: UploadCompletionResult): void {
    item.status = "done";
    item.outcome = completion.outcome;
    item.finalPath = completion.relativePath;
    item.uploadedBytes = item.sizeBytes;
    item.sessionId = null;
    item.retryAt = null;
    this.emit();
    if (completion.outcome !== FILE_UPLOAD_OUTCOME.skipped) this.options.onFileStored?.(this.snapshotOf(item));
  }

  private snapshotOf(item: QueueItem): UploadItemSnapshot {
    return this.snapshot().items.find((candidate) => candidate.id === item.id)!;
  }

  /** Starts a server session, or resumes the existing one from the bytes it already holds. */
  private async ensureSession(item: QueueItem, signal: AbortSignal): Promise<"started" | "finished"> {
    if (item.sessionId) {
      try {
        const progress = await this.transport.status(item.sessionId, signal);
        item.uploadedBytes = progress.receivedBytes;
        item.chunkLimit = progress.maxChunkBytes;
        return "started";
      } catch (error) {
        if (!(error instanceof UploadTransportError) || error.status !== 404) throw error;
        item.sessionId = null;
      }
    }
    item.uploadedBytes = 0;
    const created = await this.transport.create(
      {
        rootId: item.rootId,
        targetPath: item.targetPath,
        relativePath: item.relativePath,
        sizeBytes: item.sizeBytes,
        conflictPolicy: item.conflictPolicy,
      },
      signal,
    );
    if (created.completion) {
      this.finish(item, created.completion);
      return "finished";
    }
    item.sessionId = created.session!.id;
    item.uploadedBytes = created.session!.receivedBytes;
    item.chunkLimit = created.session!.maxChunkBytes;
    return "started";
  }

  private async sendNextChunk(item: QueueItem, signal: AbortSignal): Promise<void> {
    const offset = item.uploadedBytes;
    const end = Math.min(item.sizeBytes, offset + Math.min(this.chunkBytes, item.chunkLimit));
    let reported = 0;
    try {
      const progress = await this.transport.sendChunk(
        item.sessionId!,
        offset,
        item.file.slice(offset, end),
        (loaded) => {
          const delta = loaded - reported;
          reported = loaded;
          item.uploadedBytes = offset + loaded;
          this.recordProgress(delta);
          this.emit();
        },
        signal,
      );
      this.recordProgress(progress.receivedBytes - offset - reported);
      item.uploadedBytes = progress.receivedBytes;
      item.attempt = 0;
      this.emit();
    } catch (error) {
      // The server keeps bytes from interrupted chunks; ask it where to continue.
      item.uploadedBytes = offset;
      if (error instanceof UploadTransportError && error.problemCode === PROBLEM_CODE.uploadOffsetMismatch) {
        item.uploadedBytes = (await this.transport.status(item.sessionId!, signal)).receivedBytes;
        return;
      }
      throw error;
    }
  }

  private async withRetries<T>(item: QueueItem, signal: AbortSignal, step: () => Promise<T>): Promise<T> {
    for (;;) {
      try {
        const result = await step();
        if (item.status === "retrying") {
          item.status = item.sessionId && item.uploadedBytes >= item.sizeBytes ? "completing" : "uploading";
          item.retryAt = null;
          this.emit();
        }
        return result;
      } catch (error) {
        if (signal.aborted || !isRetryableUploadError(error) || item.attempt >= this.maxRetries) throw error;
        item.attempt += 1;
        const delay = this.backoffMs(item.attempt);
        item.status = "retrying";
        item.retryAt = this.now() + delay;
        item.error = errorMessage(error);
        this.emit();
        await this.sleep(delay, signal);
        if (item.sessionId) {
          try {
            item.uploadedBytes = (await this.transport.status(item.sessionId, signal)).receivedBytes;
          } catch (statusError) {
            if (!isRetryableUploadError(statusError)) throw statusError;
          }
        }
      }
    }
  }
}
