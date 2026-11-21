import { describe, expect, it } from "vitest";
import {
  collectDroppedFiles,
  fileListToUploadItems,
  readAllDirectoryEntries,
  traverseDroppedEntry,
  type DroppedDirectoryReader,
  type DroppedEntry,
} from "./dropped-files";

function fileEntry(name: string): DroppedEntry & { file: (ok: (file: File) => void) => void } {
  return { isFile: true, isDirectory: false, name, file: (ok) => ok(new File([name], name)) };
}

/** Directory whose reader hands out children in fixed-size batches, like Chromium (100 per call). */
function directoryEntry(name: string, children: DroppedEntry[], batchSize = 100) {
  return {
    isFile: false,
    isDirectory: true,
    name,
    readCalls: 0,
    createReader(): DroppedDirectoryReader {
      let position = 0;
      return {
        readEntries: (ok) => {
          this.readCalls += 1;
          const batch = children.slice(position, position + batchSize);
          position += batch.length;
          queueMicrotask(() => ok(batch));
        },
      };
    },
  };
}

describe("readAllDirectoryEntries", () => {
  it("keeps reading until the browser returns an empty batch", async () => {
    const children = Array.from({ length: 250 }, (_, index) => fileEntry(`IMG_${index}.HEIC`));
    const folder = directoryEntry("Camera", children);

    const entries = await readAllDirectoryEntries(folder.createReader());

    expect(entries).toHaveLength(250);
    expect(folder.readCalls).toBe(4);
  });

  it("returns nothing for an empty folder", async () => {
    expect(await readAllDirectoryEntries(directoryEntry("Empty", []).createReader())).toEqual([]);
  });

  it("propagates read errors", async () => {
    const reader: DroppedDirectoryReader = {
      readEntries: (_ok, fail) => fail?.(new DOMException("denied", "NotReadableError")),
    };
    await expect(readAllDirectoryEntries(reader)).rejects.toThrow("denied");
  });
});

describe("traverseDroppedEntry", () => {
  it("expands large nested folders completely with relative paths", async () => {
    const nested = directoryEntry("2024", Array.from({ length: 150 }, (_, index) => fileEntry(`v${index}.mov`)), 64);
    const root = directoryEntry("Trip", [nested, fileEntry("cover.jpg")]);

    const items = await traverseDroppedEntry(root);

    expect(items).toHaveLength(151);
    expect(items[0].relativePath).toBe("Trip/2024/v0.mov");
    expect(items.at(-1)?.relativePath).toBe("Trip/cover.jpg");
  });
});

describe("collectDroppedFiles", () => {
  it("falls back to the plain file list when entries are unavailable", async () => {
    const file = new File(["x"], "plain.jpg");
    const transfer = {
      items: [{ kind: "file", getAsFile: () => file }],
      files: [file],
    } as unknown as DataTransfer;

    expect(await collectDroppedFiles(transfer)).toEqual([{ file, relativePath: "plain.jpg" }]);
  });
});

describe("fileListToUploadItems", () => {
  it("keeps folder structure from folder pickers", () => {
    const file = new File(["x"], "a.jpg");
    Object.defineProperty(file, "webkitRelativePath", { value: "Album/a.jpg" });

    expect(fileListToUploadItems([file, new File(["y"], "b.jpg")]).map((item) => item.relativePath))
      .toEqual(["Album/a.jpg", "b.jpg"]);
  });
});
