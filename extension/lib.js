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

  // Possible file types of a download item. Chrome's chosen file name is authoritative when known;
  // until then, both the URL path's extension and the MIME type are hints, and either may match.
  function fileTypeCandidates(item) {
    if (!item) return [];
    const fromName = extensionOf(baseName(item.filename));
    if (baseName(item.filename)) return fromName ? [fromName] : [];
    const out = [];
    const fromUrl = extensionOf(nameFromUrl(item.finalUrl || item.url || ""));
    const fromMime = extensionFromMime(item.mime);
    if (fromUrl) out.push(fromUrl);
    if (fromMime && !out.includes(fromMime)) out.push(fromMime);
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
    const url = item.finalUrl || item.url;
    if (!isHttpUrl(url)) return false;
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
    downloadSize,
    shouldCatch,
    buildCookieHeader,
    pruneBypass,
    addBypass,
    consumeBypass,
    buildAddMessage,
  };

  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.TurboDL = api;
})(typeof self !== "undefined" ? self : globalThis);
