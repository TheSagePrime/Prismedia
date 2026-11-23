/**
 * Tracks whether files from outside the browser are being dragged over the window. Browsers fire
 * `dragenter`/`dragleave` for every child element the pointer crosses, so a simple boolean flickers;
 * counting enters against leaves gives one stable "dragging" state until the drag truly leaves.
 */

/** Whether a drag carries files from the operating system (not text or in-page elements). */
export function isFileDrag(dataTransfer: DataTransfer | null | undefined): boolean {
  return Boolean(dataTransfer && Array.from(dataTransfer.types ?? []).includes("Files"));
}

/** Enter/leave counter for one drop surface. */
export class DragDepthTracker {
  #depth = 0;

  /** Whether files are currently over the surface. */
  get active(): boolean {
    return this.#depth > 0;
  }

  /** Records a `dragenter`; returns the new active state. */
  enter(): boolean {
    this.#depth += 1;
    return this.active;
  }

  /** Records a `dragleave`; returns the new active state. */
  leave(): boolean {
    this.#depth = Math.max(0, this.#depth - 1);
    return this.active;
  }

  /** Ends the drag after a drop, an Escape, or the pointer leaving the window. */
  reset(): boolean {
    this.#depth = 0;
    return false;
  }
}
