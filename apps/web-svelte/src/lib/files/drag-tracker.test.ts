import { describe, expect, it } from "vitest";
import { DragDepthTracker, isFileDrag } from "./drag-tracker";

describe("DragDepthTracker", () => {
  it("stays active while the pointer crosses child elements and ends on the final leave", () => {
    const tracker = new DragDepthTracker();

    expect(tracker.enter()).toBe(true); // window
    expect(tracker.enter()).toBe(true); // child panel
    expect(tracker.leave()).toBe(true); // leaving the child keeps the overlay up
    expect(tracker.leave()).toBe(false); // leaving the window ends it
  });

  it("never goes negative and resets after a drop", () => {
    const tracker = new DragDepthTracker();

    expect(tracker.leave()).toBe(false);
    tracker.enter();
    tracker.enter();
    expect(tracker.reset()).toBe(false);
    expect(tracker.active).toBe(false);
    expect(tracker.enter()).toBe(true);
  });
});

describe("isFileDrag", () => {
  it("accepts drags that carry files only", () => {
    expect(isFileDrag({ types: ["Files"] } as unknown as DataTransfer)).toBe(true);
    expect(isFileDrag({ types: ["text/plain"] } as unknown as DataTransfer)).toBe(false);
    expect(isFileDrag(null)).toBe(false);
  });
});
