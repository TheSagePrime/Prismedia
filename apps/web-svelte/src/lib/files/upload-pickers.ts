/**
 * File-picker settings for Files uploads that behave well on desktop and on iPhone and iPad.
 */

/**
 * `accept` value for the photos-and-videos picker. Naming HEIC/HEIF explicitly asks Safari to hand
 * over the original photos rather than converting them to JPEG, and `.mov` covers iPhone videos
 * whose type the browser does not report.
 */
export const MEDIA_PICKER_ACCEPT = "image/*,image/heic,image/heif,.heic,.heif,video/*,.mov,.mp4,.m4v";

/** Whether the browser runs on iOS or iPadOS, where every browser uses Safari's file picker. */
export function isAppleMobile(userAgent: string, maxTouchPoints: number): boolean {
  if (/iPad|iPhone|iPod/.test(userAgent)) return true;
  // iPadOS reports itself as a Mac; a Mac with a touch screen is an iPad.
  return /Macintosh/.test(userAgent) && maxTouchPoints > 1;
}

/**
 * Whether the browser can pick a whole folder. iOS Safari exposes `webkitdirectory` but cannot
 * pick folders, so the folder button stays hidden there instead of opening a broken picker.
 */
export function supportsFolderPicker(
  environment: { userAgent: string; maxTouchPoints: number; input: HTMLInputElement } | null =
    typeof document === "undefined"
      ? null
      : { userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints ?? 0, input: document.createElement("input") },
): boolean {
  if (!environment) return false;
  return "webkitdirectory" in environment.input && !isAppleMobile(environment.userAgent, environment.maxTouchPoints);
}
