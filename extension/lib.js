// Pure helpers shared by the service worker (via importScripts) and the Node tests (via require).
// Nothing in here touches chrome.* so it can be tested with plain `node`.
(function (root) {
  "use strict";

  const HOST_NAME = "com.turbodl.host";

  // How long a URL we hand back to Chrome stays exempt from catching.
  const BYPASS_TTL_MS = 60 * 1000;

  // Normalise the app's extension list: lowercase, no leading dots, no blanks, no duplicates.
  function normalizeExtensions(list) {
    if (!Array.isArray(list)) return [];
    const out = [];
    for (const raw of list) {
      if (typeof raw !== "string") continue;
      const ext = raw.trim().replace(/^\.+/, "").toLowerCase();
      if (ext && !out.includes(ext)) out.push(ext);
    }
    return out;
  }

  // Validate a getConfig reply into the shape we cache, or null if it isn't usable.
  function parseConfigReply(reply, now) {
    if (!reply || reply.ok !== true) return null;
    const exts = normalizeExtensions(reply.catchExtensions);
    const min = Number(reply.minCatchSizeKB);
    return {
      catchExtensions: exts,
      minCatchSizeKB: Number.isFinite(min) && min > 0 ? min : 0,
      fetchedAt: now,
    };
  }

  function isHttpUrl(url) {
    if (typeof url !== "string") return false;
    try {
      const p = new URL(url).protocol;
      return p === "http:" || p === "https:";
    } catch {
      return false;
    }
  }

  // Last path segment of a Windows or POSIX path, or of a URL's path.
  function baseName(path) {
    if (typeof path !== "string" || !path) return "";
    const parts = path.split(/[\\/]/);
    return parts[parts.length - 1] || "";
  }

  function nameFromUrl(url) {
    try {
      return decodeURIComponent(baseName(new URL(url).pathname));
    } catch {
      return "";
    }
  }

  // Lowercase extension of a file name ("Setup.EXE" -> "exe", "a.tar.gz" -> "gz"), or "".
  function extensionOf(name) {
    const base = baseName(name);
    const dot = base.lastIndexOf(".");
    if (dot <= 0 || dot === base.length - 1) return "";
    return base.slice(dot + 1).toLowerCase();
  }

  // MIME type -> file extension, for downloads whose name doesn't show the type yet
  // (e.g. download.php?id=42 serving a zip). Covers the app's default catch list.
  // Generic types (application/octet-stream, text/html, ...) are deliberately absent.
  const MIME_EXTENSIONS = {
    "application/zip": "zip",
    "application/x-zip-compressed": "zip",
    "application/x-zip": "zip",
    "application/vnd.rar": "rar",
    "application/x-rar-compressed": "rar",
    "application/x-rar": "rar",
    "application/x-7z-compressed": "7z",
    "application/x-tar": "tar",
    "application/gzip": "gz",
    "application/x-gzip": "gz",
    "application/x-iso9660-image": "iso",
    "application/x-msdownload": "exe",
    "application/x-msdos-program": "exe",
    "application/x-dosexec": "exe",
    "application/vnd.microsoft.portable-executable": "exe",
    "application/x-msi": "msi",
    "application/x-ms-installer": "msi",
    "application/msix": "msix",
    "application/vnd.android.package-archive": "apk",
    "video/mp4": "mp4",
    "video/x-matroska": "mkv",
    "video/webm": "webm",
    "audio/webm": "webm",
    "video/x-msvideo": "avi",
    "video/avi": "avi",
    "video/quicktime": "mov",
    "audio/mpeg": "mp3",
    "audio/mp3": "mp3",
    "audio/flac": "flac",
    "audio/x-flac": "flac",
    "audio/mp4": "m4a",
    "audio/x-m4a": "m4a",
    "audio/wav": "wav",
    "audio/x-wav": "wav",
    "audio/wave": "wav",
    "audio/vnd.wave": "wav",
    "application/pdf": "pdf",
    "application/epub+zip": "epub",
  };

  // "Video/MP4; codecs=avc1" -> "mp4"; unknown or generic types -> "".
  function extensionFromMime(mime) {
    if (typeof mime !== "string") return "";
    const type = mime.split(";")[0].trim().toLowerCase();
    return Object.prototype.hasOwnProperty.call(MIME_EXTENSIONS, type) ? MIME_EXTENSIONS[type] : "";
  }

  // Decodes %XX escapes, or returns the text unchanged when it isn't valid percent-encoding.
  function tryDecode(text) {
    try {
      return decodeURIComponent(text);
    } catch {
      return text;
    }
  }

  // File name from a Content-Disposition value, e.g.
  //   attachment; filename*=UTF-8''na%C3%AFve%20file.zip   (RFC 6266/5987; preferred)
  //   attachment; filename="a b.zip"   or   attachment; filename=a.zip
  // Returns only the base name (no directories), or "".
  function fileNameFromDisposition(value) {
    if (typeof value !== "string" || !value) return "";
    const star = /(?:^|;)\s*filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(value);
    if (star) {
      const name = baseName(tryDecode(star[2].trim().replace(/^"(.*)"$/, "$1")));
      if (name) return name;
    }
    const plain = /(?:^|;)\s*filename\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^;]+))/i.exec(value);
    if (!plain) return "";
    const raw = plain[1] !== undefined ? plain[1].replace(/\\(.)/g, "$1") : plain[2].trim();
    return baseName(/%[0-9a-f]{2}/i.test(raw) ? tryDecode(raw) : raw);
  }

  // Query parameters that signed download URLs (S3, Azure, GitHub release assets, CDNs) use to
  // carry the file name the server will send.
  const DISPOSITION_PARAMS = ["response-content-disposition", "rscd"];
  const FILENAME_PARAMS = ["filename"];

  // File name hinted by a URL's query string, or "".
  function fileNameFromQuery(url) {
    let params;
    try {
      params = new URL(url).searchParams; // already percent-decoded once, "+" becomes a space
    } catch {
      return "";
    }
    const byName = new Map();
    for (const [key, value] of params) {
      const k = key.toLowerCase();
      if (!byName.has(k)) byName.set(k, value);
    }
    for (const key of DISPOSITION_PARAMS) {
      const name = fileNameFromDisposition(byName.get(key));
      if (name) return name;
    }
    for (const key of FILENAME_PARAMS) {
      const value = byName.get(key);
      if (value) {
        const name = baseName(value.trim());
        if (name) return name;
      }
    }
    return "";
  }

  // The URL TurboDL is given: the ORIGINAL item.url when it is http(s) (TurboDL follows redirects itself,
  // so a resume gets a fresh signed URL), else finalUrl when that is. "" for blob:, data:, file:, ...
  function downloadUrl(item) {
    if (!item) return "";
    if (isHttpUrl(item.url)) return item.url;
    if (isHttpUrl(item.finalUrl)) return item.finalUrl;
    return "";
  }

  // Name for the `add` message: Chrome's chosen name when known, else a name the URLs carry in their query
  // string (redirect target first, since that is what the server answers with). "" lets the app work it out.
  function suggestedFileName(item) {
    if (!item) return "";
    return baseName(item.filename) || fileNameFromQuery(item.finalUrl) || fileNameFromQuery(item.url) || "";
  }

  // Possible file types of a download item. Chrome's chosen file name is authoritative when known.
  // Until then every hint counts: the path extension of url and of finalUrl, a file name in either URL's
  // query string, and the MIME type.
  function fileTypeCandidates(item) {
    if (!item) return [];
    if (baseName(item.filename)) {
      const fromName = extensionOf(item.filename);
      return fromName ? [fromName] : [];
    }
    const hints = [
      extensionOf(nameFromUrl(item.url || "")),
      extensionOf(nameFromUrl(item.finalUrl || "")),
      extensionOf(fileNameFromQuery(item.finalUrl)),
      extensionOf(fileNameFromQuery(item.url)),
      extensionFromMime(item.mime),
    ];
    const out = [];
    for (const ext of hints) if (ext && !out.includes(ext)) out.push(ext);
    return out;
  }

  // Byte size of a download item, or -1 when Chrome doesn't know it yet.
  function downloadSize(item) {
    if (!item) return -1;
    for (const v of [item.totalBytes, item.fileSize]) {
      if (typeof v === "number" && v > 0) return v;
    }
    return -1;
  }

  // Decide whether a download should be handed to TurboDL.
  // config: { catchExtensions: [...], minCatchSizeKB } or null (no config -> catch nothing).
  function shouldCatch(item, config) {
    if (!item || !config) return false;
    if (!downloadUrl(item)) return false;
    const exts = normalizeExtensions(config.catchExtensions);
    if (exts.length === 0) return false;
    if (!fileTypeCandidates(item).some((ext) => exts.includes(ext))) return false;
    const size = downloadSize(item);
    if (size < 0) return true; // unknown size counts as a match
    const minBytes = (Number(config.minCatchSizeKB) || 0) * 1024;
    return size >= minBytes;
  }

  // "name=value; name2=value2", most specific path first (the order browsers send them in).
  function buildCookieHeader(cookies) {
    if (!Array.isArray(cookies)) return "";
    return cookies
      .filter((c) => c && typeof c.name === "string" && typeof c.value === "string")
      .map((c, i) => ({ c, i, pathLen: typeof c.path === "string" ? c.path.length : 0 }))
      .sort((a, b) => b.pathLen - a.pathLen || a.i - b.i)
      .map(({ c }) => (c.name ? `${c.name}=${c.value}` : c.value))
      .join("; ");
  }

  // Loop guard. A bypass list is an array of { url, expires }. Entries are kept in storage
  // so they survive a service worker restart between download() and onCreated.
  function pruneBypass(list, now) {
    return (Array.isArray(list) ? list : []).filter(
      (e) => e && typeof e.url === "string" && typeof e.expires === "number" && e.expires > now
    );
  }

  function addBypass(list, url, now, ttlMs = BYPASS_TTL_MS) {
    const next = pruneBypass(list, now);
    next.push({ url, expires: now + ttlMs });
    return next;
  }

  // Removes one live entry matching any of the given URLs.
  // Returns { matched, list } where list is the pruned list without the consumed entry.
  function consumeBypass(list, urls, now) {
    const live = pruneBypass(list, now);
    const wanted = (Array.isArray(urls) ? urls : [urls]).filter(Boolean);
    const idx = live.findIndex((e) => wanted.includes(e.url));
    if (idx < 0) return { matched: false, list: live };
    live.splice(idx, 1);
    return { matched: true, list: live };
  }

  // The `add` message for the native host (IPC contract). Optional fields are left out when empty.
  function buildAddMessage({ url, filename, referrer, userAgent, cookies, size }) {
    const msg = { type: "add", url };
    if (filename) msg.filename = filename;
    if (referrer) msg.referrer = referrer;
    if (userAgent) msg.userAgent = userAgent;
    if (cookies) msg.cookies = cookies;
    if (typeof size === "number" && size > 0) msg.size = size;
    return msg;
  }

  // The URL "Download video with TurboDL" sends, from a contextMenus OnClickData:
  // a real http(s) <video> src first (blob:/data: sources from MediaSource players are useless outside
  // the page), then the link under the cursor, then the frame (an embedded player), then the page.
  // Returns "" when none of them is http(s).
  function pickVideoUrl(info) {
    if (!info) return "";
    const candidates = [
      info.mediaType === "video" ? info.srcUrl : "",
      info.linkUrl,
      info.frameUrl,
      info.pageUrl,
    ];
    return candidates.find(isHttpUrl) || "";
  }

  // The `addVideo` message for the native host (IPC contract). Optional fields are left out when empty.
  function buildAddVideoMessage({ url, pageUrl, referrer, userAgent, cookies }) {
    const msg = { type: "addVideo", url };
    if (pageUrl) msg.pageUrl = pageUrl;
    if (referrer) msg.referrer = referrer;
    if (userAgent) msg.userAgent = userAgent;
    if (cookies) msg.cookies = cookies;
    return msg;
  }

  const api = {
    HOST_NAME,
    BYPASS_TTL_MS,
    normalizeExtensions,
    parseConfigReply,
    isHttpUrl,
    baseName,
    nameFromUrl,
    extensionOf,
    MIME_EXTENSIONS,
    extensionFromMime,
    fileTypeCandidates,
    fileNameFromDisposition,
    fileNameFromQuery,
    downloadUrl,
    suggestedFileName,
    downloadSize,
    shouldCatch,
    buildCookieHeader,
    pruneBypass,
    addBypass,
    consumeBypass,
    buildAddMessage,
    pickVideoUrl,
    buildAddVideoMessage,
  };

  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.TurboDL = api;
})(typeof self !== "undefined" ? self : globalThis);
