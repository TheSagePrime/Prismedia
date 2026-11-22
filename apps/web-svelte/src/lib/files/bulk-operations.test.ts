import { describe, expect, it } from "vitest";
import { planBulkMove, runBulkOperation, summarizeBulkResult, topLevelSelection } from "./bulk-operations";

const entry = (path: string, kind: "file" | "directory" = "file", rootId = "r1") => ({
  rootId,
  path,
  name: path.split("/").at(-1) ?? path,
  kind,
});

describe("bulk Files operations", () => {
  it("drops roots and entries inside another selected folder", () => {
    const album = entry("Photos/Album", "directory");
    const inside = entry("Photos/Album/a.jpg");
    const other = entry("Videos/b.mov");
    const otherRootSamePath = entry("Photos/Album/a.jpg", "file", "r2");

    expect(topLevelSelection([entry("", "directory"), album, inside, other, otherRootSamePath]))
      .toEqual([album, other, otherRootSamePath]);
  });

  it("plans moves into the destination and skips no-op or self-nesting moves", () => {
    const a = entry("Inbox/a.jpg");
    const already = entry("Sorted/b.jpg");
    const folder = entry("Sorted", "directory");
    const crossRoot = entry("Sorted/c.jpg", "file", "r2");

    const { moves, skipped } = planBulkMove([a, already, crossRoot], { rootId: "r1", path: "Sorted" });
    const nested = planBulkMove([folder], { rootId: "r1", path: "Sorted/2024" });

    expect(moves.map((move) => [move.item.path, move.targetRootId, move.targetPath])).toEqual([
      ["Inbox/a.jpg", "r1", "Sorted/a.jpg"],
      ["Sorted/c.jpg", "r1", "Sorted/c.jpg"],
    ]);
    expect(skipped.map((skip) => skip.reason)).toEqual(["already in this folder"]);
    expect(nested.skipped[0].reason).toBe("a folder cannot move into itself");
    expect(planBulkMove([a], { rootId: "r1", path: "" }).moves[0].targetPath).toBe("a.jpg");
  });

  it("runs every item even when some fail and reports progress", async () => {
    const progress: number[] = [];
    const result = await runBulkOperation(
      [entry("a.jpg"), entry("b.jpg"), entry("c.jpg")],
      async (item) => {
        if (item.name === "b.jpg") throw new Error("A file already exists");
      },
      (done) => progress.push(done),
    );

    expect(result.succeeded.map((item) => item.name)).toEqual(["a.jpg", "c.jpg"]);
    expect(result.failed).toEqual([{ item: entry("b.jpg"), message: "A file already exists" }]);
    expect(progress).toEqual([1, 2, 3]);
    expect(summarizeBulkResult("Moved", result)).toEqual({
      tone: "warning",
      message: "Moved 2 items. 1 item failed: b.jpg (A file already exists).",
    });
  });

  it("summarizes clean and skipped runs", () => {
    expect(summarizeBulkResult("Deleted", { succeeded: [entry("a.jpg")], failed: [] }))
      .toEqual({ tone: "success", message: "Deleted 1 item." });
    expect(summarizeBulkResult("Moved", { succeeded: [], failed: [] }, [{ item: entry("a.jpg"), reason: "already in this folder" }]).message)
      .toBe("Moved 0 items. Skipped a.jpg (already in this folder).");
  });
});
