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

// A real onCreated item from a Chrome test (7-Zip link -> GitHub release asset). The pasted finalUrl
// was elided, so its path id and signature values are placeholders; the rscd, rsct,
// response-content-disposition and response-content-type parameters are as pasted.
const GITHUB_ITEM = {
  id: 42,
  url: "https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe",
  finalUrl:
    "https://release-assets.githubusercontent.com/github-production-release-asset/466446150/c648a1d2-0000-4000-8000-000000000000" +
    "?sp=r&sv=2018-11-09&sr=b&spr=https&se=2026-11-03T06%3A10%3A00Z" +
    "&rscd=attachment%3B+filename%3D7z2605-x64.exe&rsct=application%2Foctet-stream" +
    "&skoid=96c2d410-5711-43a1-aedd-ab1947aa7ab0&sig=abc%2Fdef%3D" +
    "&jwt=eyJ0eXAiOiJKV1QifQ.e30.sig" +
    "&response-content-disposition=attachment%3B%20filename%3D7z2605-x64.exe&response-content-type=application%2Foctet-stream",
  filename: "",
  mime: "application/octet-stream",
  totalBytes: 1663506,
  fileSize: 1663506,
  state: "in_progress",
  danger: "safe",
  referrer: "https://www.7-zip.org/",
};

const realConfig = { catchExtensions: "zip rar 7z tar gz iso exe msi msix apk mp4 mkv webm avi mov mp3 flac m4a wav pdf epub".split(" "), minCatchSizeKB: 0 };

test("F1: the real GitHub release redirect is caught, with the original URL and the asset's name", () => {
  assert.equal(lib.shouldCatch(GITHUB_ITEM, realConfig), true);
  assert.equal(lib.downloadUrl(GITHUB_ITEM), "https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe");
  assert.equal(lib.suggestedFileName(GITHUB_ITEM), "7z2605-x64.exe");
  assert.ok(lib.fileTypeCandidates(GITHUB_ITEM).includes("exe"));
});

test("fileNameFromDisposition", () => {
  assert.equal(lib.fileNameFromDisposition('attachment; filename="a b.zip"'), "a b.zip");
  assert.equal(lib.fileNameFromDisposition('attachment;filename="a b.zip"'), "a b.zip");
  assert.equal(lib.fileNameFromDisposition("attachment; filename=plain.exe"), "plain.exe");
  assert.equal(lib.fileNameFromDisposition("attachment; filename=plain.exe; size=10"), "plain.exe");
  assert.equal(lib.fileNameFromDisposition("attachment; filename*=UTF-8''na%C3%AFve%20file.zip"), "na\u00efve file.zip");
  assert.equal(lib.fileNameFromDisposition("attachment; filename*=utf-8'en'%E2%82%AC%20rates.pdf"), "\u20ac rates.pdf");
  assert.equal(
    lib.fileNameFromDisposition("attachment; filename=\"fallback.zip\"; filename*=UTF-8''r%C3%A9el.zip"),
    "r\u00e9el.zip", "filename* wins over filename"
  );
  assert.equal(lib.fileNameFromDisposition('attachment; filename="say \\"hi\\".txt"'), 'say "hi".txt', "escaped quotes");
  assert.equal(lib.fileNameFromDisposition('attachment; filename="My%20Movie.mp4"'), "My Movie.mp4", "percent-encoded plain name");
  assert.equal(lib.fileNameFromDisposition('attachment; filename="100%.zip"'), "100%.zip", "a literal % is kept");
  assert.equal(lib.fileNameFromDisposition('attachment; filename="../../evil.exe"'), "evil.exe", "no directories");
  assert.equal(lib.fileNameFromDisposition('attachment; filename="..\\..\\evil.exe"'), "....evil.exe", "backslash is a quoted-pair escape (RFC 6266), so no path is left");
  assert.equal(lib.fileNameFromDisposition("attachment; filename=C:\\temp\\evil.exe"), "evil.exe", "unquoted Windows path");
  assert.equal(lib.fileNameFromDisposition("attachment; filename=../../etc/x.zip"), "x.zip");
  assert.equal(lib.fileNameFromDisposition("inline"), "");
  assert.equal(lib.fileNameFromDisposition("attachment; name=x.zip"), "", "name= is not filename=");
  assert.equal(lib.fileNameFromDisposition(""), "");
  assert.equal(lib.fileNameFromDisposition(undefined), "");
});

test("fileNameFromQuery: S3, Azure (rscd) and filename= parameters", () => {
  // S3 presigned URL as a browser sends it: the value is percent-encoded once
  assert.equal(
    lib.fileNameFromQuery("https://bucket.s3.amazonaws.com/k/obj?X-Amz-Signature=x&response-content-disposition=attachment%3Bfilename%3D%22a%20b.zip%22"),
    "a b.zip"
  );
  assert.equal(lib.fileNameFromQuery('https://s3/k?response-content-disposition=attachment;filename="a b.zip"'), "a b.zip", "unencoded");
  assert.equal(lib.fileNameFromQuery("https://acct.blob.core.windows.net/c/b?sig=x&rscd=attachment%3B+filename%3Dsetup.msi"), "setup.msi");
  assert.equal(
    lib.fileNameFromQuery("https://cdn/x?response-content-disposition=attachment%3B%20filename%2A%3DUTF-8%27%27r%25C3%25A9sum%25C3%25A9.pdf"),
    "r\u00e9sum\u00e9.pdf", "filename*= inside the parameter"
  );
  assert.equal(lib.fileNameFromQuery("https://cdn/x?Response-Content-Disposition=attachment%3Bfilename%3Dcase.zip"), "case.zip", "case-insensitive key");
  assert.equal(lib.fileNameFromQuery("https://cdn/dl?id=4&filename=report.pdf"), "report.pdf");
  assert.equal(lib.fileNameFromQuery("https://cdn/dl?filename=dir%2Fsub%2Fdeep.zip"), "deep.zip");
  assert.equal(lib.fileNameFromQuery("https://cdn/dl?file=12345"), "", "file= is not used");
  assert.equal(lib.fileNameFromQuery("https://cdn/dl?id=4"), "");
  assert.equal(lib.fileNameFromQuery("not a url"), "");
  assert.equal(lib.fileNameFromQuery(undefined), "");
});

test("downloadUrl prefers the original http(s) URL", () => {
  assert.equal(lib.downloadUrl({ url: "https://a/x.exe", finalUrl: "https://cdn/signed?sig=1" }), "https://a/x.exe");
  assert.equal(lib.downloadUrl({ url: "blob:https://a/1", finalUrl: "https://cdn/f.zip" }), "https://cdn/f.zip");
  assert.equal(lib.downloadUrl({ url: "https://a/x.exe" }), "https://a/x.exe");
  assert.equal(lib.downloadUrl({ url: "blob:https://a/1", finalUrl: "blob:https://a/1" }), "");
  assert.equal(lib.downloadUrl({ url: "data:application/zip;base64,AA==", finalUrl: "" }), "");
  assert.equal(lib.downloadUrl(null), "");
});

test("suggestedFileName: Chrome's name, then the query hints, else empty", () => {
  assert.equal(lib.suggestedFileName({ filename: "C:\\dl\\chosen.zip", finalUrl: "https://c/x?filename=other.zip" }), "chosen.zip");
  assert.equal(lib.suggestedFileName({ filename: "", url: "https://a/r", finalUrl: "https://c/x?rscd=attachment%3B+filename%3Dfinal.zip" }), "final.zip");
  assert.equal(lib.suggestedFileName({ filename: "", url: "https://a/r?filename=from-url.zip", finalUrl: "https://c/x" }), "from-url.zip");
  assert.equal(lib.suggestedFileName({ filename: "", url: "https://a/files/setup.exe", finalUrl: "https://a/files/setup.exe" }), "", "path names are left to the app");
  assert.equal(lib.suggestedFileName(null), "");
});

test("shouldCatch: redirect hints", () => {
  // only the original url has the extension (finalUrl is an opaque signed path)
  assert.equal(lib.shouldCatch({ url: "https://site/get/tool.zip", finalUrl: "https://cdn/obj/9f8e7d?sig=x", filename: "", mime: "application/octet-stream" }, config), true);
  // only finalUrl's query names the file
  assert.equal(lib.shouldCatch({ url: "https://site/dl/77", finalUrl: "https://bucket.s3/k?response-content-disposition=attachment%3B%20filename%3D%22a%20b.zip%22", filename: "" }, config), true);
  assert.equal(lib.shouldCatch({ url: "https://site/dl/77", finalUrl: "https://blob/c?rscd=attachment%3B+filename%3Dsetup.exe", filename: "" }, config), true);
  assert.equal(lib.shouldCatch({ url: "https://site/dl/77", finalUrl: "https://cdn/x?response-content-disposition=attachment%3B%20filename%2A%3DUTF-8%27%27f%25C3%25BCr.zip", filename: "" }, config), true);
  // the query names a type that isn't in the list
  assert.equal(lib.shouldCatch({ url: "https://site/dl/77", finalUrl: "https://cdn/x?rscd=attachment%3B+filename%3Dnotes.txt", filename: "" }, config), false);
  // Chrome's known name still beats every hint
  assert.equal(lib.shouldCatch({ url: "https://site/a.zip", finalUrl: "https://cdn/x?filename=a.zip", filename: "C:\\dl\\a.txt" }, config), false);
  // negative: plain octet-stream with no hints anywhere
  assert.equal(lib.shouldCatch({ url: "https://site/download?id=5", finalUrl: "https://cdn/obj/abc?sig=1", filename: "", mime: "application/octet-stream", totalBytes: 5e6 }, config), false);
});
