import { afterEach, describe, expect, it, vi } from "vitest";
import {
  excludeFile,
  fetchFileDetail,
  fileArchiveDownloadUrl,
  fileContentUrl,
  fileDownloadUrl,
  prepareFolderArchiveDownload,
  createFileUploadTransport,
  removeFileExclusion,
} from "./files";
import { FILE_UPLOAD_CONFLICT_POLICY, FILE_UPLOAD_OUTCOME, PROBLEM_CODE } from "./generated/codes";
import { UploadTransportError } from "$lib/files/upload-queue";

describe("files API", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("starts resumable uploads with the declared size and conflict choice", async () => {
    const fetchMock = mockFetch({
      session: { id: "u1", rootId: "root-1", relativePath: "Incoming/Season 1/clip.mp4", sizeBytes: "4", receivedBytes: "0", maxChunkBytes: "67108864", expiresAt: "2026-01-01T00:00:00Z" },
      completion: null,
    });
    const transport = createFileUploadTransport();

    const created = await transport.create({
      rootId: "root-1",
      targetPath: "Incoming",
      relativePath: "Season 1/clip.mp4",
      sizeBytes: 4,
      conflictPolicy: FILE_UPLOAD_CONFLICT_POLICY.keepBoth,
    }, new AbortController().signal);

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/files/uploads");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toMatchObject({ relativePath: "Season 1/clip.mp4", sizeBytes: 4, conflictPolicy: "keep-both" });
    expect(created.session).toEqual({ id: "u1", receivedBytes: 0, maxChunkBytes: 67108864 });
  });

  it("reports skipped files as an immediate completion", async () => {
    mockFetch({ session: null, completion: { outcome: FILE_UPLOAD_OUTCOME.skipped, relativePath: "a.jpg", scansQueued: "0" } });

    const created = await createFileUploadTransport().create({
      rootId: "root-1", targetPath: "", relativePath: "a.jpg", sizeBytes: 1, conflictPolicy: FILE_UPLOAD_CONFLICT_POLICY.skip,
    }, new AbortController().signal);

    expect(created).toEqual({ session: null, completion: { outcome: FILE_UPLOAD_OUTCOME.skipped, relativePath: "a.jpg" } });
  });

  it("maps problem responses and dropped connections to upload transport errors", async () => {
    mockFetchSequence([{ code: PROBLEM_CODE.fileConflict, message: "A file or folder already exists at a.jpg." }], [409]);
    const transport = createFileUploadTransport();
    const conflict = await transport.complete("u1", new AbortController().signal).catch((error: unknown) => error);
    expect(conflict).toBeInstanceOf(UploadTransportError);
    expect(conflict).toMatchObject({ status: 409, problemCode: PROBLEM_CODE.fileConflict });

    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    const dropped = await transport.status("u1", new AbortController().signal).catch((error: unknown) => error);
    expect(dropped).toMatchObject({ status: 0 });
  });

  it("builds encoded file content urls", () => {
    expect(fileContentUrl("root-1", "Season 1/clip 01.mp4")).toBe(
      "/api/files/content?rootId=root-1&path=Season+1%2Fclip+01.mp4",
    );
  });

  it("builds attachment urls for files and prepared archives", () => {
    expect(fileDownloadUrl("root-1", "Season 1/clip 01.mp4")).toBe(
      "/api/files/download?rootId=root-1&path=Season+1%2Fclip+01.mp4",
    );
    expect(fileArchiveDownloadUrl("archive-1")).toBe(
      "/api/files/archives/archive-1/content",
    );
  });

  it("starts folder preparation and reports polled compression progress", async () => {
    const fetchMock = mockFetchSequence([
      {
        id: "archive-1",
        fileName: "Season 1.zip",
        ready: false,
        progressPercent: "0",
        processedFiles: "0",
        totalFiles: "2",
        error: null,
      },
      {
        id: "archive-1",
        fileName: "Season 1.zip",
        ready: false,
        progressPercent: "50",
        processedFiles: "1",
        totalFiles: "2",
        error: null,
      },
      {
        id: "archive-1",
        fileName: "Season 1.zip",
        ready: true,
        progressPercent: "100",
        processedFiles: "2",
        totalFiles: "2",
        error: null,
      },
    ], [202, 200, 200]);
    const updates: number[] = [];

    const ready = await prepareFolderArchiveDownload("root-1", "Season 1", {
      pollIntervalMs: 0,
      onProgress: (preparation) => updates.push(preparation.progressPercent),
    });

    expect(ready.ready).toBe(true);
    expect(ready.processedFiles).toBe(2);
    expect(updates).toEqual([0, 50, 100]);
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      "/api/files/archives",
      "/api/files/archives/archive-1",
      "/api/files/archives/archive-1",
    ]);
    expect(fetchMock.mock.calls[0]?.[1]?.body).toBe(
      JSON.stringify({ rootId: "root-1", path: "Season 1" }),
    );
  });

  it("normalizes file detail directory counts", async () => {
    mockFetch({
      entry: {
        rootId: "root-1",
        path: "",
        name: "Movies",
        kind: "directory",
        sizeBytes: null,
        mimeType: null,
        modifiedAt: null,
      },
      absolutePath: "/media/movies",
      createdAt: null,
      linkedEntities: [],
      canPreview: false,
      directoryFileCount: "3",
      directoryTotalSizeBytes: "1024",
    });

    const detail = await fetchFileDetail("root-1");

    expect(detail.directoryFileCount).toBe(3);
    expect(detail.directoryTotalSizeBytes).toBe(1024);
  });

  it("marks watched-root paths as excluded", async () => {
    const fetchMock = mockFetch({ scansQueued: "1" });

    const response = await excludeFile({ rootId: "root-1", path: "Skip/movie.mkv" });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/files/exclusions");
    expect(init.method).toBe("POST");
    expect(init.body).toBe(JSON.stringify({ rootId: "root-1", path: "Skip/movie.mkv" }));
    expect(response.scansQueued).toBe(1);
  });

  it("removes watched-root path exclusions", async () => {
    const fetchMock = mockFetch({ scansQueued: 1 });

    await removeFileExclusion({ rootId: "root-1", path: "Skip/movie.mkv" });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/files/exclusions?rootId=root-1&path=Skip%2Fmovie.mkv");
    expect(init.method).toBe("DELETE");
  });
});

function mockFetch(data: unknown) {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify(data), {
    headers: { "content-type": "application/json" },
    status: 200,
  }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function mockFetchSequence(data: unknown[], statuses: number[]) {
  const responses = [...data];
  const responseStatuses = [...statuses];
  const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(responses.shift()), {
    headers: { "content-type": "application/json" },
    status: responseStatuses.shift() ?? 200,
  }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}
