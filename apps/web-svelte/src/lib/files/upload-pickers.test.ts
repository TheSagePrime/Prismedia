import { describe, expect, it } from "vitest";
import { MEDIA_PICKER_ACCEPT, isAppleMobile, supportsFolderPicker } from "./upload-pickers";

/** Input with the folder-picking property that real browsers expose (jsdom does not). */
const input = () => Object.assign(document.createElement("input"), { webkitdirectory: false });

describe("upload pickers", () => {
  it("treats iPhones and touch iPads as Apple mobile", () => {
    expect(isAppleMobile("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)", 5)).toBe(true);
    expect(isAppleMobile("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", 5)).toBe(true);
    expect(isAppleMobile("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", 0)).toBe(false);
  });

  it("offers folder picking on desktop browsers but not on iOS", () => {
    expect(supportsFolderPicker({ userAgent: "Mozilla/5.0 (X11; Linux x86_64)", maxTouchPoints: 0, input: input() })).toBe(true);
    expect(supportsFolderPicker({ userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)", maxTouchPoints: 5, input: input() })).toBe(false);
    expect(supportsFolderPicker(null)).toBe(false);
    expect(supportsFolderPicker({ userAgent: "Mozilla/5.0 (X11; Linux x86_64)", maxTouchPoints: 0, input: document.createElement("input") }))
      .toBe("webkitdirectory" in document.createElement("input"));
  });

  it("asks for original HEIC photos and iPhone videos", () => {
    expect(MEDIA_PICKER_ACCEPT).toContain("image/heic");
    expect(MEDIA_PICKER_ACCEPT).toContain("video/*");
    expect(MEDIA_PICKER_ACCEPT).toContain(".mov");
  });
});
