import { describe, expect, it, vi } from "vitest";
import { FILE_UPLOAD_CONFLICT_POLICY, FILE_UPLOAD_OUTCOME, PROBLEM_CODE } from "$lib/api/generated/codes";
import {
  UploadQueue,
  UploadTransportError,
  isRetryableUploadError,
  type UploadQueueSnapshot,
  type UploadTransport,
} from "./upload-queue";

/** In-memory server that behaves like the resumable upload API. */
class FakeServer implements UploadTransport {
  sessions = new Map<string, { relativePath: string; size: number; bytes: number[] }>();
  stored = new Map<string, number[]>();
  /** Fail the next N chunk requests after storing `keepBytes` of the chunk. */
  dropChunks = 0;
  keepBytes = 0;
  failStatus = 0;
  createCalls = 0;
  chunkCalls = 0;
  existing = new Set<string>();
  private nextId = 0;

  async create(request: Parameters<UploadTransport["create"]>[0]) {
    this.createCalls += 1;
    const path = [request.targetPath, request.relativePath].filter(Boolean).join("/");
    if (this.existing.has(path)) {
      if (request.conflictPolicy === FILE_UPLOAD_CONFLICT_POLICY.skip) {
        return { session: null, completion: { outcome: FILE_UPLOAD_OUTCOME.skipped, relativePath: path } };
      }
      if (request.conflictPolicy === FILE_UPLOAD_CONFLICT_POLICY.fail) {
        throw new UploadTransportError("A file already exists.", 409, PROBLEM_CODE.fileConflict);
      }
    }
    const id = `s${++this.nextId}`;
    this.sessions.set(id, { relativePath: path, size: request.sizeBytes, bytes: [] });
    return { session: { id, receivedBytes: 0, maxChunkBytes: 1024 }, completion: null };
  }

  async status(id: string) {
    const session = this.sessions.get(id);
    if (!session) throw new UploadTransportError("not found", 404, PROBLEM_CODE.notFound);
    return { id, receivedBytes: session.bytes.length, maxChunkBytes: 1024 };
  }

  async sendChunk(id: string, offset: number, chunk: Blob, onProgress: (loaded: number) => void, _signal?: AbortSignal) {
    this.chunkCalls += 1;
    const session = this.sessions.get(id)!;
    if (offset !== session.bytes.length) {
      throw new UploadTransportError("offset", 409, PROBLEM_CODE.uploadOffsetMismatch);
    }
    const bytes = await readBytes(chunk);
    if (this.dropChunks > 0) {
      this.dropChunks -= 1;
      session.bytes.push(...bytes.slice(0, this.keepBytes));
      throw new UploadTransportError("Network dropped", this.failStatus);
    }
    session.bytes.push(...bytes);
    onProgress(bytes.length);
    return { id, receivedBytes: session.bytes.length, maxChunkBytes: 1024 };
  }

  async complete(id: string) {
    const session = this.sessions.get(id)!;
    let path = session.relativePath;
    let outcome: (typeof FILE_UPLOAD_OUTCOME)[keyof typeof FILE_UPLOAD_OUTCOME] = FILE_UPLOAD_OUTCOME.uploaded;
    if (this.existing.has(path)) {
      path = path.replace(/(\.[^.]+)$/, " (1)$1");
      outcome = FILE_UPLOAD_OUTCOME.renamed;
    }
    this.stored.set(path, session.bytes);
    this.existing.add(path);
    this.sessions.delete(id);
    return { outcome, relativePath: path };
  }

  cancel = vi.fn(async (id: string) => {
    this.sessions.delete(id);
  });
}

function readBytes(blob: Blob): Promise<number[]> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve([...new Uint8Array(reader.result as ArrayBuffer)]);
    reader.onerror = () => reject(reader.error);
    reader.readAsArrayBuffer(blob);
  });
}

function file(name: string, size: number): File {
  return new File([new Uint8Array(Array.from({ length: size }, (_, index) => index % 256))], name);
}

function createQueue(server: FakeServer, extra: Partial<ConstructorParameters<typeof UploadQueue>[0]> = {}) {
  const snapshots: UploadQueueSnapshot[] = [];
  const sleeps: number[] = [];
  const queue = new UploadQueue({
    transport: server,
    chunkBytes: 100,
    concurrency: 2,
    maxRetries: 3,
    backoffMs: (attempt) => attempt * 1000,
    sleep: async (ms) => {
      sleeps.push(ms);
    },
    onChange: (snapshot) => snapshots.push(snapshot),
    ...extra,
  });
  return { queue, snapshots, sleeps };
}

async function settle(queue: UploadQueue): Promise<UploadQueueSnapshot> {
  await vi.waitFor(() => expect(queue.snapshot().active).toBe(false));
  return queue.snapshot();
}

const batch = { rootId: "root", targetPath: "Incoming", conflictPolicy: FILE_UPLOAD_CONFLICT_POLICY.fail };

describe("UploadQueue", () => {
  it("uploads each file in ordered chunks and reports per-file and total progress", async () => {
    const server = new FakeServer();
    const stored = vi.fn();
    const { queue, snapshots } = createQueue(server, { onFileStored: stored });

    queue.add([{ file: file("a.jpg", 250), relativePath: "Trip/a.jpg" }, { file: file("b.mov", 30), relativePath: "b.mov" }], batch);
    const final = await settle(queue);

    expect(final.items.map((item) => item.error)).toEqual([null, null]);
    expect(final.counts.done).toBe(2);
    expect(final.uploadedBytes).toBe(280);
    expect(server.stored.get("Incoming/Trip/a.jpg")).toHaveLength(250);
    expect(server.chunkCalls).toBe(4);
    expect(stored).toHaveBeenCalledTimes(2);
    const progress = snapshots.map((snapshot) => snapshot.items[0]?.uploadedBytes ?? 0);
    expect(progress).toContain(100);
    expect(progress).toContain(200);
  });

  it("retries dropped chunks with backoff and resumes from the bytes the server kept", async () => {
    const server = new FakeServer();
    server.dropChunks = 2;
    server.keepBytes = 40;
    const { queue, sleeps } = createQueue(server);

    queue.add([{ file: file("clip.mp4", 150), relativePath: "clip.mp4" }], batch);
    const final = await settle(queue);

    expect(final.counts.done).toBe(1);
    expect(sleeps).toEqual([1000, 2000]);
    expect(server.stored.get("Incoming/clip.mp4")).toEqual(Array.from({ length: 150 }, (_, index) => index % 256));
    expect(server.createCalls).toBe(1);
  });

  it("marks a file failed after the retry budget, keeps the rest of the batch going, and resumes on retry", async () => {
    const server = new FakeServer();
    server.dropChunks = 4;
    server.failStatus = 503;
    const { queue } = createQueue(server, { concurrency: 1 });

    const [first] = queue.add([
      { file: file("bad.mov", 150), relativePath: "bad.mov" },
      { file: file("good.jpg", 20), relativePath: "good.jpg" },
    ], batch);
    let snapshot = await settle(queue);

    expect(snapshot.counts.failed).toBe(1);
    expect(snapshot.counts.done).toBe(1);
    expect(snapshot.items.find((item) => item.id === first)?.error).toBe("Network dropped");

    queue.retry(first);
    snapshot = await settle(queue);

    expect(snapshot.counts.done).toBe(2);
    expect(server.createCalls).toBe(2);
    expect(server.stored.get("Incoming/bad.mov")).toHaveLength(150);
  });

  it("does not retry permanent failures such as conflicts or a full disk", async () => {
    const server = new FakeServer();
    server.existing.add("Incoming/taken.jpg");
    const { queue, sleeps } = createQueue(server);

    queue.add([{ file: file("taken.jpg", 10), relativePath: "taken.jpg" }], batch);
    const final = await settle(queue);

    expect(final.counts.failed).toBe(1);
    expect(sleeps).toEqual([]);
    expect(isRetryableUploadError(new UploadTransportError("full", 507))).toBe(false);
    expect(isRetryableUploadError(new UploadTransportError("offline", 0))).toBe(true);
  });

  it("reports skipped and renamed files from the conflict choice", async () => {
    const server = new FakeServer();
    server.existing.add("Incoming/a.jpg");
    const { queue } = createQueue(server);

    queue.add([{ file: file("a.jpg", 10), relativePath: "a.jpg" }], { ...batch, conflictPolicy: FILE_UPLOAD_CONFLICT_POLICY.skip });
    queue.add([{ file: file("a.jpg", 10), relativePath: "a.jpg" }], { ...batch, conflictPolicy: FILE_UPLOAD_CONFLICT_POLICY.keepBoth });
    const final = await settle(queue);

    expect(final.counts.skipped).toBe(1);
    expect(final.counts.renamed).toBe(1);
    expect(final.items.map((item) => item.finalPath)).toEqual(["Incoming/a.jpg", "Incoming/a (1).jpg"]);
  });

  it("re-syncs with the server after an offset mismatch", async () => {
    const server = new FakeServer();
    const original = server.sendChunk.bind(server);
    let first = true;
    server.sendChunk = async (id: string, offset: number, chunk: Blob, onProgress: (loaded: number) => void, signal?: AbortSignal) => {
      if (first) {
        first = false;
        server.sessions.get(id)!.bytes.push(...new Array(100).fill(0).map((_, index) => index));
        throw new UploadTransportError("offset", 409, PROBLEM_CODE.uploadOffsetMismatch);
      }
      return original(id, offset, chunk, onProgress, signal);
    };
    const { queue } = createQueue(server);

    queue.add([{ file: file("x.bin", 150), relativePath: "x.bin" }], batch);
    const final = await settle(queue);

    expect(final.counts.done).toBe(1);
    expect(server.stored.get("Incoming/x.bin")).toHaveLength(150);
  });

  it("cancels a file and discards its server session", async () => {
    const server = new FakeServer();
    let release: () => void = () => undefined;
    const original = server.sendChunk.bind(server);
    server.sendChunk = (id: string, offset: number, chunk: Blob, onProgress: (loaded: number) => void, signal?: AbortSignal) =>
      new Promise<Awaited<ReturnType<typeof original>>>((resolve, reject) => {
        signal!.addEventListener("abort", () => reject(signal!.reason));
        release = () => void original(id, offset, chunk, onProgress, signal).then(resolve, reject);
      });
    const { queue } = createQueue(server);

    const [id] = queue.add([{ file: file("big.mkv", 300), relativePath: "big.mkv" }], batch);
    await vi.waitFor(() => expect(server.sessions.size).toBe(1));
    queue.cancel(id);
    release();
    const final = await settle(queue);

    expect(final.counts.cancelled).toBe(1);
    expect(server.cancel).toHaveBeenCalledTimes(1);
    expect(server.stored.size).toBe(0);
    expect(final.totalBytes).toBe(0);
  });
});
