/**
 * Planning and running bulk Files operations (move and delete) over a multi-selection. Planning is
 * pure so it can be tested; running is sequential so the server and the library scanner see one
 * change at a time, and every item reports its own success or failure.
 */
import { FILE_ENTRY_KIND } from "$lib/api/generated/codes";
import type { FileTreeNodeMeta } from "./file-tree-state";

type Entry = Pick<FileTreeNodeMeta, "rootId" | "path" | "name" | "kind">;

/** One planned move: the source entry and its root-relative destination path. */
export interface PlannedMove<T extends Entry> {
  item: T;
  targetRootId: string;
  targetPath: string;
}

/** An entry left out of a bulk operation, with a short reason for the summary. */
export interface SkippedEntry<T extends Entry> {
  item: T;
  reason: string;
}

/** Result of running a bulk operation item by item. */
export interface BulkRunResult<T> {
  succeeded: T[];
  failed: { item: T; message: string }[];
}

function parentOf(path: string): string {
  return path.split("/").filter(Boolean).slice(0, -1).join("/");
}

function isSameOrInside(ancestor: string, path: string): boolean {
  return ancestor === "" || path === ancestor || path.startsWith(`${ancestor}/`);
}

/**
 * Drops library roots and entries that sit inside another selected folder: acting on the folder
 * already covers them, and acting twice would fail or double-count.
 */
export function topLevelSelection<T extends Entry>(items: readonly T[]): T[] {
  const candidates = items.filter((item) => item.path !== "");
  return candidates.filter((item) =>
    !candidates.some((other) =>
      other !== item &&
      other.rootId === item.rootId &&
      other.kind === FILE_ENTRY_KIND.directory &&
      item.path.startsWith(`${other.path}/`)));
}

/**
 * Plans moving a selection into a destination folder. Entries already in that folder, and folders
 * that would move into themselves, are skipped with a reason instead of failing on the server.
 */
export function planBulkMove<T extends Entry>(
  items: readonly T[],
  target: Pick<FileTreeNodeMeta, "rootId" | "path">,
): { moves: PlannedMove<T>[]; skipped: SkippedEntry<T>[] } {
  const moves: PlannedMove<T>[] = [];
  const skipped: SkippedEntry<T>[] = [];
  for (const item of topLevelSelection(items)) {
    const sameRoot = item.rootId === target.rootId;
    if (sameRoot && parentOf(item.path) === target.path) {
      skipped.push({ item, reason: "already in this folder" });
      continue;
    }
    if (sameRoot && item.kind === FILE_ENTRY_KIND.directory && isSameOrInside(item.path, target.path)) {
      skipped.push({ item, reason: "a folder cannot move into itself" });
      continue;
    }
    moves.push({
      item,
      targetRootId: target.rootId,
      targetPath: [target.path, item.name].filter(Boolean).join("/"),
    });
  }
  return { moves, skipped };
}

/** Runs one async step per item in order, collecting each item's success or failure. */
export async function runBulkOperation<T>(
  items: readonly T[],
  step: (item: T) => Promise<unknown>,
  onProgress?: (done: number, total: number) => void,
): Promise<BulkRunResult<T>> {
  const result: BulkRunResult<T> = { succeeded: [], failed: [] };
  for (const [index, item] of items.entries()) {
    try {
      await step(item);
      result.succeeded.push(item);
    } catch (error) {
      result.failed.push({ item, message: error instanceof Error ? error.message : "Operation failed" });
    }
    onProgress?.(index + 1, items.length);
  }
  return result;
}

function count(n: number, noun: string): string {
  return `${n.toLocaleString()} ${noun}${n === 1 ? "" : "s"}`;
}

/** Plain-language summary of a finished bulk operation, naming what failed or was skipped. */
export function summarizeBulkResult<T extends Entry>(
  verb: "Moved" | "Deleted",
  result: BulkRunResult<T>,
  skipped: readonly SkippedEntry<T>[] = [],
): { tone: "success" | "warning" | "error"; message: string } {
  const parts = [`${verb} ${count(result.succeeded.length, "item")}.`];
  if (result.failed.length) {
    const names = result.failed.slice(0, 3).map(({ item, message }) => `${item.name} (${message})`).join("; ");
    const more = result.failed.length > 3 ? ` and ${result.failed.length - 3} more` : "";
    parts.push(`${count(result.failed.length, "item")} failed: ${names}${more}.`);
  }
  if (skipped.length) {
    const names = skipped.slice(0, 3).map(({ item, reason }) => `${item.name} (${reason})`).join("; ");
    const more = skipped.length > 3 ? ` and ${skipped.length - 3} more` : "";
    parts.push(`Skipped ${names}${more}.`);
  }
  const tone = result.failed.length
    ? result.succeeded.length ? "warning" : "error"
    : skipped.length ? "warning" : "success";
  return { tone, message: parts.join(" ") };
}
