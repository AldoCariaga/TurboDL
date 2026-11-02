// Run with: node extension/test/lib.test.js
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const lib = require("../lib.js");

const config = { catchExtensions: ["zip", "exe", "mp4", "gz"], minCatchSizeKB: 100 };
const item = (o) => ({ id: 1, url: "https://example.com/files/setup.exe", filename: "", totalBytes: -1, fileSize: -1, ...o });

test("extensionOf", () => {
  assert.equal(lib.extensionOf("Setup.EXE"), "exe");
  assert.equal(lib.extensionOf("C:\\Users\\x\\Downloads\\a.tar.gz"), "gz");
  assert.equal(lib.extensionOf("/home/x/movie.mp4"), "mp4");
  assert.equal(lib.extensionOf("README"), "");
  assert.equal(lib.extensionOf(".bashrc"), "");
  assert.equal(lib.extensionOf("trailing."), "");
  assert.equal(lib.extensionOf(""), "");
  assert.equal(lib.extensionOf(undefined), "");
});

test("nameFromUrl", () => {
  assert.equal(lib.nameFromUrl("https://h/a/b/My%20File.zip?x=1#y"), "My File.zip");
  assert.equal(lib.nameFromUrl("https://h/"), "");
  assert.equal(lib.nameFromUrl("https://h/%E0%A4%A"), "", "bad escapes don't throw");
  assert.equal(lib.nameFromUrl("nonsense"), "");
});

test("extensionFromMime", () => {
  assert.equal(lib.extensionFromMime("application/zip"), "zip");
  assert.equal(lib.extensionFromMime("Video/MP4; codecs=\"avc1\""), "mp4");
  assert.equal(lib.extensionFromMime("application/x-msdownload"), "exe");
  assert.equal(lib.extensionFromMime("application/vnd.android.package-archive"), "apk");
  for (const generic of ["application/octet-stream", "text/html", "text/plain", "binary/octet-stream", "", undefined, null]) {
    assert.equal(lib.extensionFromMime(generic), "", String(generic));
  }
  assert.equal(lib.extensionFromMime("constructor"), "", "no prototype lookups");
});

test("MIME table covers every type in the app's default catch list", () => {
  const defaults = "zip rar 7z tar gz iso exe msi msix apk mp4 mkv webm avi mov mp3 flac m4a wav pdf epub".split(" ");
  const mapped = new Set(Object.values(lib.MIME_EXTENSIONS));
  for (const ext of defaults) assert.ok(mapped.has(ext), ext);
  for (const [mime, ext] of Object.entries(lib.MIME_EXTENSIONS)) {
    assert.equal(mime, mime.toLowerCase(), mime);
    assert.ok(defaults.includes(ext), `${mime} -> ${ext}`);
  }
});

test("fileTypeCandidates", () => {
  assert.deepEqual(lib.fileTypeCandidates(item({ filename: "C:\\d\\real.zip", mime: "video/mp4" })), ["zip"], "Chrome's name wins");
  assert.deepEqual(lib.fileTypeCandidates(item({ filename: "C:\\d\\README", mime: "application/zip" })), [], "known name without a type");
  assert.deepEqual(lib.fileTypeCandidates(item({ url: "https://e/download.php?id=4", mime: "application/zip" })), ["php", "zip"]);
  assert.deepEqual(lib.fileTypeCandidates(item({ url: "https://e/get", mime: "application/octet-stream" })), []);
  assert.deepEqual(lib.fileTypeCandidates(item({ url: "https://e/a.zip", mime: "application/zip" })), ["zip"], "no duplicates");
  assert.deepEqual(lib.fileTypeCandidates(item({ url: "https://h/redirect", finalUrl: "https://cdn/f.iso" })), ["iso"], "finalUrl path");
  assert.deepEqual(lib.fileTypeCandidates(null), []);
});

test("downloadSize treats 0 and -1 as unknown", () => {
  assert.equal(lib.downloadSize(item({ totalBytes: 5000 })), 5000);
  assert.equal(lib.downloadSize(item({ totalBytes: 0, fileSize: 42 })), 42);
  assert.equal(lib.downloadSize(item({ totalBytes: 0, fileSize: 0 })), -1);
  assert.equal(lib.downloadSize(item({})), -1);
  assert.equal(lib.downloadSize(null), -1);
});

test("isHttpUrl", () => {
  assert.ok(lib.isHttpUrl("http://a/b"));
  assert.ok(lib.isHttpUrl("HTTPS://a/b"));
  for (const u of ["blob:https://a/uuid", "data:text/plain,hi", "file:///C:/x.zip", "ftp://a/x.zip", "chrome://downloads", "not a url", "", null]) {
    assert.equal(lib.isHttpUrl(u), false, String(u));
  }
});

test("normalizeExtensions", () => {
  assert.deepEqual(lib.normalizeExtensions([".ZIP", " exe ", "zip", "", 3, null, "..gz"]), ["zip", "exe", "gz"]);
  assert.deepEqual(lib.normalizeExtensions("zip exe"), []);
  assert.deepEqual(lib.normalizeExtensions(undefined), []);
});

test("shouldCatch: extension match", () => {
  assert.equal(lib.shouldCatch(item({}), config), true);
  assert.equal(lib.shouldCatch(item({ url: "https://e/x.ZIP" }), config), true);
  assert.equal(lib.shouldCatch(item({ url: "https://e/page.html" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "https://e/download.php?id=4" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "https://e/noext" }), config), false);
  // Chrome's chosen name wins over the URL
  assert.equal(lib.shouldCatch(item({ url: "https://e/get?id=1", filename: "D:\\dl\\video.mp4" }), config), true);
  assert.equal(lib.shouldCatch(item({ url: "https://e/x.zip", filename: "D:\\dl\\x.txt" }), config), false);
  // MIME fallback: only while Chrome's name is unknown, only for mapped types in the list
  assert.equal(lib.shouldCatch(item({ url: "https://e/download.php?id=4", mime: "application/zip" }), config), true);
  assert.equal(lib.shouldCatch(item({ url: "https://e/get", mime: "video/mp4; codecs=avc1" }), config), true);
  assert.equal(lib.shouldCatch(item({ url: "https://e/get", mime: "application/pdf" }), config), false, "pdf not in this list");
  assert.equal(lib.shouldCatch(item({ url: "https://e/get", mime: "application/octet-stream" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "https://e/get", mime: "text/html" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "https://e/get", mime: "" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "https://e/page.html", mime: "application/zip", filename: "C:\\d\\page.html" }), config), false, "known name beats MIME");
  assert.equal(lib.shouldCatch(item({ url: "https://e/get", mime: "application/zip", totalBytes: 1024 }), config), false, "size still applies");
  // config entries with dots/caps still match
  assert.equal(lib.shouldCatch(item({}), { catchExtensions: [".EXE"], minCatchSizeKB: 0 }), true);
});

test("shouldCatch: size threshold", () => {
  assert.equal(lib.shouldCatch(item({ totalBytes: 100 * 1024 }), config), true, "exactly the minimum");
  assert.equal(lib.shouldCatch(item({ totalBytes: 100 * 1024 - 1 }), config), false, "just below");
  assert.equal(lib.shouldCatch(item({ totalBytes: -1 }), config), true, "unknown size counts");
  assert.equal(lib.shouldCatch(item({ totalBytes: 0, fileSize: 0 }), config), true, "unknown size counts");
  assert.equal(lib.shouldCatch(item({ totalBytes: 1 }), { ...config, minCatchSizeKB: 0 }), true, "0 = catch all sizes");
});

test("shouldCatch: no config, empty config, bad schemes", () => {
  assert.equal(lib.shouldCatch(item({}), null), false);
  assert.equal(lib.shouldCatch(item({}), { catchExtensions: [], minCatchSizeKB: 0 }), false);
  assert.equal(lib.shouldCatch(item({ url: "blob:https://e/abc.exe" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "data:application/zip;base64,AA==" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "file:///C:/x/setup.exe" }), config), false);
  assert.equal(lib.shouldCatch(item({ url: "blob:x", finalUrl: "https://e/a.zip" }), config), true, "finalUrl decides");
});

test("parseConfigReply", () => {
  assert.deepEqual(lib.parseConfigReply({ ok: true, catchExtensions: ["ZIP", ".exe"], minCatchSizeKB: 512 }, 7),
    { catchExtensions: ["zip", "exe"], minCatchSizeKB: 512, fetchedAt: 7 });
  assert.deepEqual(lib.parseConfigReply({ ok: true }, 7), { catchExtensions: [], minCatchSizeKB: 0, fetchedAt: 7 });
  assert.equal(lib.parseConfigReply({ ok: true, catchExtensions: [], minCatchSizeKB: -5 }, 1).minCatchSizeKB, 0);
  assert.equal(lib.parseConfigReply({ ok: true, catchExtensions: [], minCatchSizeKB: "abc" }, 1).minCatchSizeKB, 0);
  assert.equal(lib.parseConfigReply({ ok: false, error: "x" }, 1), null);
  assert.equal(lib.parseConfigReply(null, 1), null);
  assert.equal(lib.parseConfigReply(undefined, 1), null);
});

test("buildCookieHeader", () => {
  assert.equal(lib.buildCookieHeader([]), "");
  assert.equal(lib.buildCookieHeader(undefined), "");
  assert.equal(lib.buildCookieHeader([{ name: "a", value: "1", path: "/" }]), "a=1");
  // longer (more specific) paths first; equal paths keep Chrome's order
  assert.equal(
    lib.buildCookieHeader([
      { name: "root", value: "r", path: "/" },
      { name: "deep", value: "d", path: "/files/private" },
      { name: "mid", value: "m", path: "/files" },
      { name: "root2", value: "r2", path: "/" },
    ]),
    "deep=d; mid=m; root=r; root2=r2"
  );
  // values are passed through untouched; nameless cookies send only the value
  assert.equal(lib.buildCookieHeader([{ name: "t", value: "a=b==", path: "/" }, { name: "", value: "solo", path: "/" }]), "t=a=b==; solo");
  assert.equal(lib.buildCookieHeader([null, { name: "x" }, { name: "ok", value: "1" }]), "ok=1");
});

test("loop guard: add, consume once, expire", () => {
  const url = "https://e/setup.exe";
  let list = lib.addBypass([], url, 1000);
  assert.equal(list.length, 1);
  assert.equal(list[0].expires, 1000 + lib.BYPASS_TTL_MS);

  let r = lib.consumeBypass(list, [url], 1500);
  assert.equal(r.matched, true);
  assert.equal(r.list.length, 0);
  r = lib.consumeBypass(r.list, [url], 1600);
  assert.equal(r.matched, false, "an entry is used only once");

  list = lib.addBypass([], url, 0);
  r = lib.consumeBypass(list, url, lib.BYPASS_TTL_MS + 1);
  assert.equal(r.matched, false, "expired entries don't match");
  assert.equal(r.list.length, 0, "expired entries are pruned");
});

test("loop guard: matches url or finalUrl, other urls untouched, duplicates counted", () => {
  let list = lib.addBypass([], "https://a/1.zip", 0);
  list = lib.addBypass(list, "https://a/2.zip", 0);
  list = lib.addBypass(list, "https://a/2.zip", 0);
  let r = lib.consumeBypass(list, ["https://redirect/x", "https://a/2.zip"], 10);
  assert.equal(r.matched, true);
  assert.deepEqual(r.list.map((e) => e.url), ["https://a/1.zip", "https://a/2.zip"]);
  r = lib.consumeBypass(r.list, [undefined, "https://other/"], 10);
  assert.equal(r.matched, false);
  assert.equal(r.list.length, 2);
  assert.deepEqual(lib.pruneBypass([null, { url: 1 }, { url: "u", expires: 5 }, { url: "v", expires: 50 }], 10).map((e) => e.url), ["v"]);
});

test("buildAddMessage leaves out empty optional fields", () => {
  assert.deepEqual(lib.buildAddMessage({ url: "https://e/a.zip" }), { type: "add", url: "https://e/a.zip" });
  assert.deepEqual(
    lib.buildAddMessage({ url: "u", filename: "a.zip", referrer: "r", userAgent: "ua", cookies: "c=1", size: 10 }),
    { type: "add", url: "u", filename: "a.zip", referrer: "r", userAgent: "ua", cookies: "c=1", size: 10 }
  );
  assert.deepEqual(lib.buildAddMessage({ url: "u", filename: "", referrer: "", cookies: "", size: -1 }), { type: "add", url: "u" });
});
