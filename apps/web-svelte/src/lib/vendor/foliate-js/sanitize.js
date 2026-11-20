// Prismedia local addition (not upstream foliate-js): strips script vectors from book
// section documents before they are serialized into same-origin blob: URLs. Book frames
// keep `allow-same-origin` (the reader drives their DOM) and `allow-scripts` (WebKit event
// delivery), so book content must never be able to execute script on the app origin.

// Nested frames/objects stay: their same-origin targets are book items that pass through
// this sanitizer too, and other targets are cross-origin or opaque.
const SCRIPTABLE_ELEMENTS = new Set(['script', 'applet', 'portal', 'base'])

// Attributes that carry a navigable/loadable URL and must not use a script scheme.
const URL_ATTRIBUTES = new Set(['href', 'src', 'action', 'formaction', 'data', 'poster', 'background', 'lowsrc', 'dynsrc', 'cite', 'longdesc', 'xlink:href', 'srcset', 'ping'])

// SVG animation attributes that can assign a URL value to another attribute.
const ANIMATION_ELEMENTS = new Set(['animate', 'set', 'animatemotion', 'animatetransform'])
const ANIMATION_VALUE_ATTRIBUTES = new Set(['to', 'from', 'values', 'by'])

// Control characters and whitespace that browsers ignore when parsing a URL scheme.
// eslint-disable-next-line no-control-regex
const IGNORED_URL_CHARS = /[\x00-\x20\x7f-\x9f]/g
const SCRIPT_SCHEME = /^(?:javascript|vbscript|livescript):/i
const SCRIPT_SCHEME_ANYWHERE = /(?:javascript|vbscript|livescript):/i
const GENERIC_XML_TYPE = /^(?:application|text)\/(?:[\w.+-]+\+)?xml$/

/**
 * The DOMParser type a book item must be parsed (and sanitized) as before it may be
 * handed to a frame, or null for types browsers never render as a scriptable document.
 * Media types are compared case-insensitively and without parameters, because the
 * browser renders `Text/HTML; charset=utf-8` exactly like `text/html`.
 */
export const documentParseType = mediaType => {
    if (typeof mediaType !== 'string') return null
    const base = mediaType.split(';')[0].trim().toLowerCase()
    if (base === 'text/html') return 'text/html'
    if (base === 'application/xhtml+xml') return 'application/xhtml+xml'
    if (base === 'image/svg+xml') return 'image/svg+xml'
    if (GENERIC_XML_TYPE.test(base)) return 'application/xml'
    return null
}

const PASSIVE_RESOURCE_TYPE = /^(?:image\/(?!svg)[\w.+-]+|audio\/[\w.+-]+|video\/[\w.+-]+|font\/[\w.+-]+|text\/css|application\/(?:font-woff2?|x-font-[\w.+-]+|vnd\.ms-opentype|font-sfnt))$/

/**
 * The blob type to use for a book resource that is NOT parsed and sanitized. Only passive
 * types (raster images, audio, video, fonts, CSS) keep their declared type; anything else
 * becomes `application/octet-stream`, so an unexpected or missing media type can never
 * make a frame render unsanitized markup as a same-origin document.
 */
export const passiveResourceType = mediaType => {
    if (typeof mediaType !== 'string') return 'application/octet-stream'
    const base = mediaType.split(';')[0].trim().toLowerCase()
    return PASSIVE_RESOURCE_TYPE.test(base) ? base : 'application/octet-stream'
}

/** Whether a URL-ish attribute value would resolve to a script-executing scheme. */
export const isScriptUrl = value =>
    typeof value === 'string' && SCRIPT_SCHEME.test(value.replace(IGNORED_URL_CHARS, ''))

const stripsAttribute = (element, attr) => {
    const name = attr.name.toLowerCase()
    const localName = attr.localName?.toLowerCase() ?? name
    if (localName.startsWith('on')) return true
    if (name === 'srcdoc' || localName === 'srcdoc') return true
    if (URL_ATTRIBUTES.has(name) || URL_ATTRIBUTES.has(localName)) return isScriptUrl(attr.value)
    if (ANIMATION_ELEMENTS.has(element.localName?.toLowerCase())) {
        if (ANIMATION_VALUE_ATTRIBUTES.has(localName))
            return SCRIPT_SCHEME_ANYWHERE.test(attr.value.replace(IGNORED_URL_CHARS, ''))
        // Never let animation retarget an event handler or URL-bearing attribute.
        if (localName === 'attributename') {
            const target = attr.value.trim().toLowerCase().replace(/^xlink:/, '')
            return target.startsWith('on') || URL_ATTRIBUTES.has(target)
        }
    }
    return false
}

const isScriptMeta = element =>
    element.localName?.toLowerCase() === 'meta'
    && (element.getAttribute('http-equiv') ?? '').trim().toLowerCase() === 'refresh'

/**
 * Removes executable content from an element subtree in place: script-capable elements,
 * meta refresh, inline event handlers, `srcdoc`, and script-scheme URLs (including via SVG
 * animation). Returns the same root for chaining.
 */
export const sanitizeBookTree = root => {
    if (!root) return root
    const elements = [root, ...root.getElementsByTagName('*')]
    for (const element of elements) {
        if (element !== root && !root.contains(element)) continue
        const name = element.localName?.toLowerCase()
        if (element !== root && (SCRIPTABLE_ELEMENTS.has(name) || isScriptMeta(element))) {
            element.remove()
            continue
        }
        for (const attr of Array.from(element.attributes))
            if (stripsAttribute(element, attr)) element.removeAttributeNode(attr)
    }
    return root
}

/**
 * Removes executable content from a parsed book document in place: everything
 * {@link sanitizeBookTree} removes, plus non-CSS `xml-stylesheet` processing instructions
 * (XSLT can generate script after this pass runs). Returns the same document.
 */
export const sanitizeBookDocument = doc => {
    if (!doc) return doc

    for (let child = doc.firstChild; child; ) {
        const next = child.nextSibling
        if (child.nodeType === 7 /* PROCESSING_INSTRUCTION_NODE */
            && child.target?.toLowerCase() === 'xml-stylesheet'
            && !/type\s*=\s*["']text\/css["']/i.test(child.data ?? '')) child.remove()
        child = next
    }

    const root = doc.documentElement
    if (root && SCRIPTABLE_ELEMENTS.has(root.localName?.toLowerCase())) {
        root.remove()
        return doc
    }
    sanitizeBookTree(root)
    return doc
}
