import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { documentParseType, sanitizeBookDocument, sanitizeBookTree } from "$lib/vendor/foliate-js/sanitize.js";
import { EPUB } from "$lib/vendor/foliate-js/epub.js";

const XHTML = "application/xhtml+xml";

function parse(markup: string, type: DOMParserSupportedType = "text/html"): Document {
  return new DOMParser().parseFromString(markup, type);
}

describe("sanitizeBookDocument", () => {
  it("removes script elements in HTML, XHTML and SVG namespaces", () => {
    const doc = parse(
      `<html xmlns="http://www.w3.org/1999/xhtml"><head><script>a()</script></head><body>
        <p>Text</p><script src="x.js"></script>
        <svg xmlns="http://www.w3.org/2000/svg"><script>b()</script><circle r="1"/></svg>
      </body></html>`,
      XHTML,
    );

    sanitizeBookDocument(doc);

    expect(doc.getElementsByTagName("script")).toHaveLength(0);
    expect(doc.getElementsByTagNameNS("http://www.w3.org/2000/svg", "script")).toHaveLength(0);
    expect(doc.querySelector("p")?.textContent).toBe("Text");
    expect(doc.getElementsByTagNameNS("http://www.w3.org/2000/svg", "circle")).toHaveLength(1);
  });

  it("strips inline event handlers and srcdoc but keeps benign attributes", () => {
    const doc = parse(
      `<body onload="x()"><img src="a.png" alt="A" onerror="y()" ONMOUSEOVER="z()">` +
        `<iframe srcdoc="<script>w()</script>" title="frame"></iframe></body>`,
    );

    sanitizeBookDocument(doc);

    const img = doc.querySelector("img")!;
    expect(doc.body.hasAttribute("onload")).toBe(false);
    expect(img.hasAttribute("onerror")).toBe(false);
    expect(img.hasAttribute("onmouseover")).toBe(false);
    expect(img.getAttribute("src")).toBe("a.png");
    expect(img.getAttribute("alt")).toBe("A");
    expect(doc.querySelector("iframe")?.hasAttribute("srcdoc")).toBe(false);
    expect(doc.querySelector("iframe")?.getAttribute("title")).toBe("frame");
  });

  it("removes script-scheme URLs even when obfuscated with whitespace or control characters", () => {
    const doc = parse(
      `<a id="a" href="javascript:x()">1</a>` +
        `<a id="b" href=" \tJaVa\nScript:x()">2</a>` +
        `<a id="c" href="\u0001javascript:x()">3</a>` +
        `<form id="d" action="vbscript:x"></form>` +
        `<a id="ok" href="chapter2.xhtml#n1">4</a>` +
        `<a id="ext" href="https://example.com/">5</a>`,
    );

    sanitizeBookDocument(doc);

    for (const id of ["a", "b", "c"]) expect(doc.getElementById(id)?.hasAttribute("href")).toBe(false);
    expect(doc.getElementById("d")?.hasAttribute("action")).toBe(false);
    expect(doc.getElementById("ok")?.getAttribute("href")).toBe("chapter2.xhtml#n1");
    expect(doc.getElementById("ext")?.getAttribute("href")).toBe("https://example.com/");
  });

  it("neutralizes SVG xlink:href and animation tricks", () => {
    const doc = parse(
      `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
        <a id="l" xlink:href="javascript:x()"><text>t</text></a>
        <a id="m"><set attributeName="href" to="javascript:x()"/><animate attributeName="xlink:href" values="#a;javascript:x()"/></a>
        <animate id="ok" attributeName="opacity" from="0" to="1"/>
      </svg>`,
      "image/svg+xml",
    );

    sanitizeBookDocument(doc);

    expect(doc.getElementById("l")?.hasAttributeNS("http://www.w3.org/1999/xlink", "href")).toBe(false);
    const set = doc.getElementsByTagNameNS("http://www.w3.org/2000/svg", "set")[0];
    expect(set.hasAttribute("to")).toBe(false);
    expect(set.hasAttribute("attributeName")).toBe(false);
    const animate = doc.getElementsByTagNameNS("http://www.w3.org/2000/svg", "animate")[0];
    expect(animate.hasAttribute("values")).toBe(false);
    expect(doc.getElementById("ok")?.getAttribute("to")).toBe("1");
  });

  it("drops meta refresh, base and non-CSS xml-stylesheet instructions", () => {
    const doc = parse(
      `<?xml-stylesheet type="text/xsl" href="evil.xsl"?><?xml-stylesheet type="text/css" href="style.css"?>` +
        `<html xmlns="http://www.w3.org/1999/xhtml"><head><meta http-equiv="refresh" content="0;url=javascript:x()"/>` +
        `<meta charset="utf-8"/><base href="https://example.com/"/></head><body/></html>`,
      XHTML,
    );

    sanitizeBookDocument(doc);

    const instructions = Array.from(doc.childNodes).filter(
      (node): node is ProcessingInstruction => node.nodeType === Node.PROCESSING_INSTRUCTION_NODE,
    );
    expect(instructions.map((node) => node.data)).toEqual([`type="text/css" href="style.css"`]);
    expect(doc.querySelector("meta[http-equiv]")).toBeNull();
    expect(doc.querySelector("meta[charset]")).not.toBeNull();
    expect(doc.querySelector("base")).toBeNull();
  });

  it("sanitizes a detached element tree for converted formats", () => {
    const root = document.createElement("section");
    root.innerHTML = `<a href="javascript:x()" onclick="y()">n</a><script>z()</script>`;

    sanitizeBookTree(root);

    expect(root.innerHTML).toBe("<a>n</a>");
  });
});

describe("documentParseType", () => {
  it("normalizes every renderable document spelling and ignores inert types", () => {
    expect(documentParseType("text/html")).toBe("text/html");
    expect(documentParseType("Text/HTML; charset=utf-8")).toBe("text/html");
    expect(documentParseType("APPLICATION/XHTML+XML")).toBe(XHTML);
    expect(documentParseType("image/svg+xml")).toBe("image/svg+xml");
    expect(documentParseType("application/xml")).toBe("application/xml");
    expect(documentParseType("text/xml")).toBe("application/xml");
    expect(documentParseType("application/x-dtbook+xml")).toBe("application/xml");
    expect(documentParseType("image/jpeg")).toBeNull();
    expect(documentParseType("text/css")).toBeNull();
    expect(documentParseType(undefined)).toBeNull();
  });
});

describe("EPUB loader", () => {
  const blobs: Blob[] = [];

  beforeEach(() => {
    blobs.length = 0;
    vi.spyOn(URL, "createObjectURL").mockImplementation((blob) => {
      blobs.push(blob as Blob);
      return `blob:test/${blobs.length}`;
    });
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  const chapter = `<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml"><head><title>c</title><script src="app.js"></script></head>
<body><p>Readable text</p><script>window.parent.__bookScript = true</script>
<img src="missing.png" onerror="window.parent.__bookOnerror = true"/>
<a href="javascript:window.parent.__bookLink = true">link</a>
<iframe src="data.xml"></iframe></body></html>`;

  const dataXml = `<?xml version="1.0"?><root><script xmlns="http://www.w3.org/1999/xhtml">x()</script><v>1</v></root>`;

  const files: Record<string, string> = {
    "META-INF/container.xml": `<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>`,
    "OEBPS/content.opf": `<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">x</dc:identifier><dc:title>T</dc:title></metadata>
<manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="js" href="app.js" media-type="text/javascript"/><item id="d" href="data.xml" media-type="application/xml"/></manifest>
<spine><itemref idref="c1"/></spine></package>`,
    "OEBPS/c1.xhtml": chapter,
    "OEBPS/app.js": "window.parent.__bookExternal = true",
    "OEBPS/data.xml": dataXml,
  };

  it("serializes section documents without scripts, handlers or script links", async () => {
    const book = await new EPUB({
      loadText: async (name: string) => files[name] ?? null,
      loadBlob: async (name: string) => (files[name] === undefined ? null : new Blob([files[name]])),
      getSize: (name: string) => files[name]?.length ?? 0,
    }).init();

    const serialized: Promise<unknown>[] = [];
    book.transformTarget.addEventListener("data", (event: CustomEvent<{ data: unknown }>) => {
      serialized.push(Promise.resolve(event.detail.data));
    });

    const url = await book.sections[0].load();
    expect(url).toMatch(/^blob:test\//);

    const texts = (await Promise.all(serialized)).map((data) => (typeof data === "string" ? data : ""));
    const section = texts.find((text) => text.includes("Readable text"))!;
    expect(section).toBeDefined();
    expect(section).not.toMatch(/<script/i);
    expect(section).not.toMatch(/onerror/i);
    expect(section).not.toMatch(/javascript:/i);
    expect(texts.some((text) => text.includes("__bookExternal"))).toBe(false);
    const nestedXml = texts.find((text) => text.includes("<v>1</v>"));
    expect(nestedXml).toBeDefined();
    expect(nestedXml).not.toMatch(/<script/i);
  });
});
