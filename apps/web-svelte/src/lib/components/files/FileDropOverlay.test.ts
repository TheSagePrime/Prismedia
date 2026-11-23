import { fireEvent, render, screen } from "@testing-library/svelte";
import { describe, expect, it, vi } from "vitest";
import FileDropOverlay from "./FileDropOverlay.svelte";

function fileDrag(type: string): Event {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(event, "dataTransfer", { value: { types: ["Files"], dropEffect: "none" } });
  return event;
}

describe("FileDropOverlay", () => {
  it("shows the destination while files are dragged over the window, without flickering", async () => {
    // Reduced motion removes the overlay without an exit animation, so its removal is observable.
    vi.stubGlobal("matchMedia", (query: string) => ({ matches: query.includes("reduce"), media: query, addEventListener() {}, removeEventListener() {} }));
    render(FileDropOverlay, { props: { targetName: "Trip 2026", onDrop: vi.fn() } });

    await fireEvent(window, fileDrag("dragenter"));
    await fireEvent(window, fileDrag("dragenter"));
    expect(screen.getByText("Drop files or folders to upload")).toBeTruthy();
    expect(screen.getByText("Trip 2026")).toBeTruthy();

    await fireEvent(window, fileDrag("dragleave"));
    expect(screen.queryByText("Drop files or folders to upload")).toBeTruthy();
    await fireEvent(window, fileDrag("dragleave"));
    await vi.waitFor(() => expect(screen.queryByText("Drop files or folders to upload")).toBeNull());
    vi.unstubAllGlobals();
  });

  it("hands dropped files to the upload queue", async () => {
    const onDrop = vi.fn();
    render(FileDropOverlay, { props: { targetName: "Photos", onDrop } });

    await fireEvent(window, fileDrag("dragenter"));
    const drop = fileDrag("drop");
    await fireEvent(window, drop);

    expect(onDrop).toHaveBeenCalledTimes(1);
    expect(drop.defaultPrevented).toBe(true);
  });

  it("explains read-only folders and refuses the drop", async () => {
    const onDrop = vi.fn();
    render(FileDropOverlay, { props: { targetName: "Radarr movies", readOnly: true, onDrop } });

    await fireEvent(window, fileDrag("dragenter"));
    expect(screen.getByText("Uploads aren't allowed here")).toBeTruthy();
    await fireEvent(window, fileDrag("drop"));

    expect(onDrop).not.toHaveBeenCalled();
  });

  it("ignores drags that carry no files", async () => {
    render(FileDropOverlay, { props: { targetName: "Photos", onDrop: vi.fn() } });
    const textDrag = new Event("dragenter", { bubbles: true, cancelable: true });
    Object.defineProperty(textDrag, "dataTransfer", { value: { types: ["text/plain"] } });

    await fireEvent(window, textDrag);

    expect(screen.queryByText("Drop files or folders to upload")).toBeNull();
  });
});
