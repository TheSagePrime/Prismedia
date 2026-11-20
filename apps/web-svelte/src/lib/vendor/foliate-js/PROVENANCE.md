# foliate-js (vendored)

Reflowable/fixed book rendering engine used by Prismedia's book reader for EPUB
(and, in Phase 2, PDF). Vendored rather than installed because foliate-js is not
published to npm.

- Source: https://github.com/johnfactotum/foliate-js
- Pinned commit: `78914aef4466eb960965702401634c2cb348e9b1`
- License: MIT (see `LICENSE`)

Embedding entry point is `view.js`, which defines the `<foliate-view>` custom
element. `vendor/` holds foliate's own bundled dependencies (zip.js, fflate).
Prismedia uses foliate **only for reflowable EPUB**; PDFs are rendered to page
images server-side and shown in the comic reader, so foliate never opens a PDF.

Local modifications (re-apply when updating the pinned commit):

- `pdf.js` is **stubbed** — it throws instead of rendering. foliate's PDF path pulled in a
  large vendored pdfjs build (and a top-level `await` our build target rejects); since we
  don't use it, `vendor/pdfjs/` was removed and `pdf.js` reduced to a stub that keeps
  `makeBook()`'s dynamic import resolvable.

- `sanitize.js` is a **Prismedia addition**. `epub.js`, `mobi.js` and `fb2.js` call it to
  strip script elements, event-handler attributes, script-scheme URLs and non-CSS
  `xml-stylesheet` instructions from section documents before they are serialized to
  same-origin blob: URLs. `epub.js` also routes every renderable HTML/XHTML/SVG/XML media
  type (any spelling) through that path, refuses book-supplied script items, and never
  falls back to an unsanitized blob for self-referencing documents. Look for
  `// Prismedia:` comments when re-applying.

Dev/build files (`reader.js`, `reader.html`, `tests/`, `rollup/`, config) were not
vendored. Update by re-copying the runtime modules from a newer pinned commit.
