// The foliate-js reader engine is vendored under src/lib/vendor and excluded from
// type-checking. Declare its entry module so the dynamic import in BookFileReader
// resolves; the `<foliate-view>` element is used through a loose runtime handle.
declare module "$lib/vendor/foliate-js/view.js";
declare module "$lib/vendor/foliate-js/sanitize.js" {
  export function sanitizeBookDocument(doc: Document): Document;
  export function sanitizeBookTree<T extends Element>(root: T): T;
  export function documentParseType(mediaType: string | undefined | null): string | null;
  export function isScriptUrl(value: string): boolean;
  export function passiveResourceType(mediaType: string | undefined | null): string;
}
declare module "$lib/vendor/foliate-js/epub.js";
