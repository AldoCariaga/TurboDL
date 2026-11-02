// Run with: node extension/test/background.test.js
// Loads the real background.js into a sandbox with a fake `chrome` API and drives its listeners.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "..");
const EXT_ID = "bmffkkpdabfjihjbmhmammbckmeaoblf";
const UA = "Mozilla/5.0 TestBrowser";
// Objects made inside the sandbox have that realm's prototypes; copy them out before comparing.
const plain = (v) => (v === undefined ? v : JSON.parse(JSON.stringify(v)));

function event() {
  const listeners = [];
  return {
    listeners,
    addListener: (fn) => listeners.push(fn),
    fire: (...args) => listeners.map((fn) => fn(...args)),
  };
}

function storageArea(name, onChanged, data) {
  return {
    data,
    async get(keys) {
      const list = typeof keys === "string" ? [keys] : keys;
      const out = {};
      for (const k of list) if (k in data) out[k] = structuredClone(data[k]);
      return out;
    },
    async set(obj) {
      const changes = {};
      for (const [k, v] of Object.entries(obj)) {
        changes[k] = { oldValue: data[k], newValue: v };
        data[k] = plain(v);
      }
      onChanged.fire(changes, name);
    },
    async remove(keys) {
      const changes = {};
      for (const k of typeof keys === "string" ? [keys] : keys) {
        if (!(k in data)) continue;
        changes[k] = { oldValue: data[k] };
        delete data[k];
      }
      if (Object.keys(changes).length) onChanged.fire(changes, name);
    },
  };
}

// A fake browser. `persist` carries storage across "service worker restarts".
function makeBrowser({ persist = { local: {}, session: {}, alarms: {} }, cookies = [], host } = {}) {
  const calls = { native: [], cancel: [], erase: [], download: [], menus: [], badge: [] };
  const onChanged = event();
  let nextId = 100;
  const chrome = {
    runtime: {
      id: EXT_ID,
      onInstalled: event(),
      onStartup: event(),
      onMessage: event(),
      async sendNativeMessage(name, msg) {
        calls.native.push({ name, msg: plain(msg) });
        return browser.host(msg);
      },
    },
    storage: {
      local: storageArea("local", onChanged, persist.local),
      session: storageArea("session", onChanged, persist.session),
      onChanged,
    },
    downloads: {
      onCreated: event(),
      async cancel(id) { calls.cancel.push(id); },
      async erase(q) { calls.erase.push(plain(q)); return [q.id]; },
      async download(opts) {
        const id = nextId++;
        calls.download.push(plain(opts));
        // A broken loop guard would re-catch and re-download forever; fail fast instead of hanging.
        if (calls.download.length > 20) throw new Error("download loop: the loop guard is not working");
        // Real Chrome fires onCreated for extension-started downloads too.
        if (browser.echoDownloads) {
          chrome.downloads.onCreated.fire({
            id, url: opts.url, finalUrl: opts.url, filename: "", state: "in_progress",
            totalBytes: -1, fileSize: -1, referrer: "",
            ...(browser.markExtensionDownloads ? { byExtensionId: EXT_ID } : {}),
          });
        }
        return id;
      },
    },
    cookies: {
      async getAll(q) { calls.cookiesFor = q.url; return cookies; },
    },
    contextMenus: {
      onClicked: event(),
      removeAll(cb) { calls.menus = []; cb && cb(); },
      create(item) { calls.menus.push(plain(item)); },
    },
    action: {
      async setBadgeText(o) { calls.badge.push(o.text); },
      async setBadgeBackgroundColor(o) { calls.badgeColor = o.color; },
      async setTitle() {},
    },
    alarms: {
      onAlarm: event(),
      async get(name) { return persist.alarms[name]; },
      async create(name, info) { persist.alarms[name] = { name, ...info }; },
    },
  };

  const browser = {
    chrome, calls, persist,
    host: host || (() => { throw new Error("Specified native messaging host not found."); }),
    echoDownloads: true,
    markExtensionDownloads: true,
  };

  const sandbox = {
    chrome,
    navigator: { userAgent: UA },
    console: { log() {}, warn() {}, error() {} },
    URL, structuredClone, setTimeout, clearTimeout,
  };
  sandbox.self = sandbox;
  sandbox.importScripts = (...files) => {
    for (const f of files) vm.runInContext(fs.readFileSync(path.join(root, f), "utf8"), sandbox, { filename: f });
  };
  vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(path.join(root, "background.js"), "utf8"), sandbox, { filename: "background.js" });
  return browser;
}

async function flush() {
  for (let i = 0; i < 30; i++) await new Promise((r) => setImmediate(r));
}

const goodConfig = { ok: true, catchExtensions: ["zip", "exe"], minCatchSizeKB: 100 };

function hostWith(config, onAdd = () => ({ ok: true })) {
  return (msg) => (msg.type === "getConfig" ? config : onAdd(msg));
}

function download(o) {
  return {
    id: 1, url: "https://example.com/files/setup.exe", finalUrl: "https://example.com/files/setup.exe",
    filename: "", state: "in_progress", totalBytes: -1, fileSize: -1, referrer: "https://example.com/page", ...o,
  };
}

async function installed(opts) {
  const b = makeBrowser(opts);
  b.chrome.runtime.onInstalled.fire({ reason: "install" });
  await flush();
  return b;
}

test("install: link menu + video menu, refresh alarm, config fetched and cached", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  assert.equal(b.calls.menus.length, 2);
  assert.deepEqual(b.calls.menus[0], {
    id: "turbodl-download-link", title: "Download with TurboDL", contexts: ["link"],
    targetUrlPatterns: ["http://*/*", "https://*/*"],
  }, "link menu unchanged");
  assert.equal(b.calls.menus[1].id, "turbodl-download-video");
  assert.equal(b.calls.menus[1].title, "Download video with TurboDL");
  assert.deepEqual(b.calls.menus[1].contexts, ["page", "video", "link", "frame"]);
  assert.equal(b.persist.alarms["turbodl-refresh-config"].periodInMinutes, 10);
  assert.deepEqual(b.calls.native[0], { name: "com.turbodl.host", msg: { type: "getConfig" } });
  assert.deepEqual(b.persist.local.config.catchExtensions, ["zip", "exe"]);
  assert.equal(b.persist.local.config.minCatchSizeKB, 100);
  assert.equal(b.persist.local.hostStatus.reachable, true);
  assert.deepEqual(b.calls.badge, [""], "catching defaults to ON");
});

test("matching download is cancelled, erased and sent with cookies, referrer and UA", async () => {
  const cookies = [{ name: "sid", value: "abc", path: "/" }, { name: "dl", value: "9", path: "/files" }];
  const b = await installed({ host: hostWith(goodConfig), cookies });
  b.chrome.downloads.onCreated.fire(download({ id: 7, totalBytes: 500 * 1024 }));
  await flush();
  assert.deepEqual(b.calls.cancel, [7]);
  assert.deepEqual(b.calls.erase, [{ id: 7 }]);
  assert.equal(b.calls.cookiesFor, "https://example.com/files/setup.exe");
  assert.deepEqual(b.calls.native.at(-1).msg, {
    type: "add",
    url: "https://example.com/files/setup.exe",
    referrer: "https://example.com/page",
    userAgent: UA,
    cookies: "dl=9; sid=abc",
    size: 500 * 1024,
  });
  assert.equal(b.calls.download.length, 0, "no fallback when the host accepts it");
});

test("the original URL is sent and Chrome's file name is passed when known", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  b.chrome.downloads.onCreated.fire(download({ url: "https://e/get?id=1", finalUrl: "https://cdn.e/x.zip", filename: "C:\\Users\\u\\Downloads\\x.zip" }));
  await flush();
  const msg = b.calls.native.at(-1).msg;
  assert.equal(msg.url, "https://e/get?id=1");
  assert.equal(msg.filename, "x.zip");
  assert.equal(msg.size, undefined, "unknown size is left out");
});

test("non-matching, too small, non-http and finished downloads stay in Chrome", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  const before = b.calls.native.length;
  b.chrome.downloads.onCreated.fire(download({ url: "https://e/page.html", finalUrl: "https://e/page.html" }));
  b.chrome.downloads.onCreated.fire(download({ totalBytes: 10 * 1024 }));
  b.chrome.downloads.onCreated.fire(download({ url: "blob:https://e/1234", finalUrl: "blob:https://e/1234", filename: "C:\\d\\a.zip" }));
  b.chrome.downloads.onCreated.fire(download({ url: "data:application/zip;base64,AA==", finalUrl: "" }));
  b.chrome.downloads.onCreated.fire(download({ url: "file:///C:/a.zip", finalUrl: "file:///C:/a.zip" }));
  b.chrome.downloads.onCreated.fire(download({ state: "complete" }));
  await flush();
  assert.equal(b.calls.cancel.length, 0);
  assert.equal(b.calls.native.length, before);
});

test("catching OFF leaves everything to Chrome and shows the badge", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  await b.chrome.storage.local.set({ catching: false });
  await flush();
  assert.equal(b.calls.badge.at(-1), "off");
  b.chrome.downloads.onCreated.fire(download({}));
  await flush();
  assert.equal(b.calls.cancel.length, 0);
});

test("host unreachable on add: download restarts in Chrome and is not caught again", async () => {
  let addCalls = 0;
  const b = await installed({ host: hostWith(goodConfig, () => { addCalls++; throw new Error("Native host has exited."); }) });
  b.chrome.downloads.onCreated.fire(download({ id: 3 }));
  await flush();
  assert.equal(addCalls, 1);
  assert.deepEqual(b.calls.download, [{ url: "https://example.com/files/setup.exe" }]);
  assert.deepEqual(b.calls.cancel, [3], "only the original was cancelled, not the fallback");
  assert.equal(b.persist.local.hostStatus.reachable, false);
  assert.match(b.persist.local.hostStatus.error, /exited/);
  assert.deepEqual(b.persist.session.bypass, [], "bypass entry used up");
});

test("loop guard works by URL even without byExtensionId, and only once", async () => {
  let fail = true;
  const b = await installed({ host: hostWith(goodConfig, () => (fail ? { ok: false, error: "busy" } : { ok: true })) });
  b.markExtensionDownloads = false;
  b.chrome.downloads.onCreated.fire(download({ id: 4 }));
  await flush();
  assert.equal(b.calls.download.length, 1, "fell back once on ok:false");
  assert.deepEqual(b.calls.cancel, [4], "fallback was not caught");
  assert.equal(b.persist.local.hostStatus.error, "busy");
  fail = false;
  b.chrome.downloads.onCreated.fire(download({ id: 5 }));
  await flush();
  assert.deepEqual(b.calls.cancel, [4, 5], "a later download of the same URL is caught again");
});

test("bypass entries survive a service worker restart", async () => {
  const b = await installed({ host: hostWith(goodConfig, () => ({ ok: false })) });
  b.echoDownloads = false; // the fallback's onCreated arrives after the restart
  b.chrome.downloads.onCreated.fire(download({ id: 6 }));
  await flush();
  assert.equal(b.persist.session.bypass.length, 1);
  const b2 = makeBrowser({ persist: b.persist, host: hostWith(goodConfig) });
  b2.chrome.downloads.onCreated.fire(download({ id: 60 }));
  await flush();
  assert.equal(b2.calls.cancel.length, 0, "restarted worker still lets the fallback through");
  assert.equal(b2.calls.native.length, 0);
});

test("no cache and host unreachable: nothing is caught", async () => {
  const b = await installed({});
  assert.equal(b.persist.local.config, undefined);
  assert.equal(b.persist.local.hostStatus.reachable, false);
  b.chrome.downloads.onCreated.fire(download({}));
  await flush();
  assert.equal(b.calls.cancel.length, 0);
});

test("cached config is kept when a refresh fails, and used after a restart", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  const cached = b.persist.local.config;
  b.host = () => { throw new Error("gone"); };
  b.chrome.alarms.onAlarm.fire({ name: "turbodl-refresh-config" });
  await flush();
  assert.equal(b.calls.native.at(-1).msg.type, "getConfig", "alarm triggers a refresh");
  assert.deepEqual(b.persist.local.config, cached);
  const b2 = makeBrowser({ persist: b.persist, host: hostWith(goodConfig) });
  b2.chrome.runtime.onStartup.fire();
  await flush();
  b2.chrome.downloads.onCreated.fire(download({}));
  await flush();
  assert.equal(b2.calls.cancel.length, 1);
});

test("startup refreshes config; alarm is not duplicated", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  const alarm = b.persist.alarms["turbodl-refresh-config"];
  const b2 = makeBrowser({ persist: b.persist, host: hostWith({ ok: true, catchExtensions: ["iso"], minCatchSizeKB: 0 }) });
  b2.chrome.runtime.onStartup.fire();
  await flush();
  assert.equal(b2.persist.alarms["turbodl-refresh-config"], alarm);
  assert.deepEqual(b2.persist.local.config.catchExtensions, ["iso"]);
});

test("context menu sends the link with page referrer and cookies", async () => {
  const b = await installed({ host: hostWith(goodConfig), cookies: [{ name: "a", value: "1", path: "/" }] });
  b.chrome.contextMenus.onClicked.fire(
    { menuItemId: "turbodl-download-link", linkUrl: "https://e/get?id=9", pageUrl: "https://e/list" },
    { url: "https://e/list" }
  );
  await flush();
  assert.deepEqual(b.calls.native.at(-1).msg, {
    type: "add", url: "https://e/get?id=9", referrer: "https://e/list", userAgent: UA, cookies: "a=1",
  });
  assert.equal(b.calls.download.length, 0);
});

test("context menu failure: no Chrome download, red ! badge until the next successful call", async () => {
  let addOk = false;
  const b = await installed({ host: hostWith(goodConfig, () => { if (addOk) return { ok: true }; throw new Error("no host"); }) });
  const click = (linkUrl) =>
    b.chrome.contextMenus.onClicked.fire({ menuItemId: "turbodl-download-link", linkUrl, pageUrl: "https://e/" }, {});

  click("https://e/a.zip");
  await flush();
  assert.equal(b.calls.download.length, 0, "not handed to Chrome");
  assert.equal(b.persist.local.menuError.url, "https://e/a.zip");
  assert.equal(b.persist.local.menuError.kind, "link");
  assert.equal(b.persist.local.menuError.error, "no host");
  assert.equal(b.calls.badge.at(-1), "!");
  assert.equal(b.calls.badgeColor, "#c42b1c");

  // ok:false is a failure too
  b.host = hostWith(goodConfig, () => ({ ok: false, error: "Add dialog is busy" }));
  click("https://e/b.zip");
  await flush();
  assert.equal(b.persist.local.menuError.error, "Add dialog is busy");
  assert.equal(b.calls.download.length, 0);

  // the next successful native call of any kind clears it
  b.host = hostWith(goodConfig);
  b.chrome.alarms.onAlarm.fire({ name: "turbodl-refresh-config" });
  await flush();
  assert.equal(b.persist.local.menuError, undefined);
  assert.equal(b.calls.badge.at(-1), "");

  // with catching off, clearing the error goes back to the grey "off" badge
  await b.chrome.storage.local.set({ catching: false });
  b.host = () => { throw new Error("down"); };
  click("https://e/c.zip");
  await flush();
  assert.equal(b.calls.badge.at(-1), "!", "error beats off");
  await b.chrome.storage.local.remove("menuError"); // what the popup does once it has shown the error
  await flush();
  assert.equal(b.calls.badge.at(-1), "off");

  click("javascript:alert(1)");
  await flush();
  assert.equal(b.persist.local.menuError, undefined, "non-http links are ignored");
});

test("MIME fallback catches download.php style links", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  b.chrome.downloads.onCreated.fire(download({ id: 11, url: "https://e/download.php?id=42", finalUrl: "https://e/download.php?id=42", mime: "application/zip" }));
  b.chrome.downloads.onCreated.fire(download({ id: 12, url: "https://e/get?id=1", finalUrl: "https://e/get?id=1", mime: "application/octet-stream" }));
  await flush();
  assert.deepEqual(b.calls.cancel, [11]);
  const add = b.calls.native.at(-1).msg;
  assert.equal(add.url, "https://e/download.php?id=42");
  assert.equal(add.filename, undefined, "name left to the app when Chrome hasn't chosen one");
});

test("popup refresh message replies with the result", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  const reply = await new Promise((resolve) => {
    const keepOpen = b.chrome.runtime.onMessage.listeners[0]({ type: "refreshConfig" }, { id: EXT_ID }, resolve);
    assert.equal(keepOpen, true);
  });
  assert.deepEqual(plain(reply), { ok: true });
  const ignored = b.chrome.runtime.onMessage.listeners[0]({ type: "refreshConfig" }, { id: "someone-else" }, () => {});
  assert.equal(ignored, false, "messages from other senders are ignored");
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

test("F1: the real GitHub redirect is caught and sent with the github.com URL and the asset name", async () => {
  const cookies = [{ name: "logged_in", value: "no", path: "/" }];
  const b = await installed({ host: hostWith({ ok: true, catchExtensions: ["zip", "exe"], minCatchSizeKB: 0 }), cookies });
  b.chrome.downloads.onCreated.fire({ ...GITHUB_ITEM });
  await flush();
  assert.deepEqual(b.calls.cancel, [42]);
  assert.deepEqual(b.calls.erase, [{ id: 42 }]);
  assert.equal(b.calls.cookiesFor, GITHUB_ITEM.url, "cookies for the URL that is sent");
  assert.deepEqual(b.calls.native.at(-1).msg, {
    type: "add",
    url: "https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe",
    filename: "7z2605-x64.exe",
    referrer: "https://www.7-zip.org/",
    userAgent: UA,
    cookies: "logged_in=no",
    size: 1663506,
  });
});

test("F1: a failed redirect download goes back to Chrome by its original URL, and isn't re-caught", async () => {
  const b = await installed({ host: hostWith({ ok: true, catchExtensions: ["exe"], minCatchSizeKB: 0 }, () => ({ ok: false, error: "x" })) });
  b.markExtensionDownloads = false; // rely on the URL guard alone
  b.chrome.downloads.onCreated.fire({ ...GITHUB_ITEM });
  await flush();
  assert.deepEqual(b.calls.download, [{ url: GITHUB_ITEM.url }]);
  assert.deepEqual(b.calls.cancel, [42], "the fallback wasn't caught");
});

test("F1: the loop guard also matches when Chrome reports the fallback by its redirect target", async () => {
  const b = await installed({ host: hostWith({ ok: true, catchExtensions: ["exe"], minCatchSizeKB: 0 }, () => ({ ok: false })) });
  b.echoDownloads = false;
  b.chrome.downloads.onCreated.fire({ ...GITHUB_ITEM });
  await flush();
  assert.equal(b.persist.session.bypass.length, 1);
  // The fallback shows up with url = the redirect target this time (url and finalUrl both checked).
  b.chrome.downloads.onCreated.fire({ ...GITHUB_ITEM, id: 43, url: "https://other/redirect", finalUrl: GITHUB_ITEM.url });
  await flush();
  assert.deepEqual(b.calls.cancel, [42]);
});

const VIDEO = "turbodl-download-video";

test("video menu on a MediaSource player sends addVideo with the page URL, cookies and UA", async () => {
  const b = await installed({ host: hostWith(goodConfig), cookies: [{ name: "SID", value: "s", path: "/" }] });
  const page = "https://www.youtube.com/watch?v=abc";
  b.chrome.contextMenus.onClicked.fire(
    { menuItemId: VIDEO, mediaType: "video", srcUrl: "blob:https://www.youtube.com/5a1b", pageUrl: page },
    { url: page }
  );
  await flush();
  assert.deepEqual(b.calls.native.at(-1), {
    name: "com.turbodl.host",
    msg: { type: "addVideo", url: page, pageUrl: page, referrer: page, userAgent: UA, cookies: "SID=s" },
  });
  assert.equal(b.calls.cookiesFor, page);
  assert.equal(b.persist.local.menuError, undefined);
  assert.equal(b.calls.download.length, 0);
});

test("video menu prefers a real video src, and takes cookies for that URL", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  b.chrome.contextMenus.onClicked.fire(
    { menuItemId: VIDEO, mediaType: "video", srcUrl: "https://cdn.e/clip.mp4", linkUrl: "https://e/l", pageUrl: "https://e/post" },
    { url: "https://e/post" }
  );
  await flush();
  const msg = b.calls.native.at(-1).msg;
  assert.equal(msg.url, "https://cdn.e/clip.mp4");
  assert.equal(msg.pageUrl, "https://e/post");
  assert.equal(msg.referrer, "https://e/post");
  assert.equal(b.calls.cookiesFor, "https://cdn.e/clip.mp4");
});

test("video menu in an embedded player frame sends the frame URL, page URL from the tab", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  b.chrome.contextMenus.onClicked.fire(
    { menuItemId: VIDEO, frameUrl: "https://player.vimeo.com/video/1", pageUrl: "https://blog.e/post" },
    { url: "https://blog.e/post" }
  );
  await flush();
  const msg = b.calls.native.at(-1).msg;
  assert.equal(msg.url, "https://player.vimeo.com/video/1");
  assert.equal(msg.pageUrl, "https://blog.e/post");
});

test("video menu: no tab falls back to info.pageUrl; nothing http(s) sends nothing", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  b.chrome.contextMenus.onClicked.fire({ menuItemId: VIDEO, linkUrl: "https://e/v/9", pageUrl: "https://e/list" }, undefined);
  await flush();
  assert.equal(b.calls.native.at(-1).msg.pageUrl, "https://e/list");
  const before = b.calls.native.length;
  b.chrome.contextMenus.onClicked.fire({ menuItemId: VIDEO, pageUrl: "chrome://newtab/" }, { url: "chrome://newtab/" });
  await flush();
  assert.equal(b.calls.native.length, before);
});

test("video menu failure: red ! badge and a video error, no Chrome download, cleared on success", async () => {
  const b = await installed({ host: hostWith(goodConfig, () => ({ ok: false, error: "yt-dlp is missing" })) });
  b.chrome.contextMenus.onClicked.fire({ menuItemId: VIDEO, pageUrl: "https://e/watch" }, { url: "https://e/watch" });
  await flush();
  const err = b.persist.local.menuError;
  assert.deepEqual({ kind: err.kind, url: err.url, error: err.error }, { kind: "video", url: "https://e/watch", error: "yt-dlp is missing" });
  assert.equal(b.calls.badge.at(-1), "!");
  assert.equal(b.calls.download.length, 0);
  assert.equal(b.calls.cancel.length, 0);

  b.host = () => { throw new Error("Specified native messaging host not found."); };
  b.chrome.contextMenus.onClicked.fire({ menuItemId: VIDEO, pageUrl: "https://e/watch2" }, { url: "https://e/watch2" });
  await flush();
  assert.match(b.persist.local.menuError.error, /not found/);

  b.host = hostWith(goodConfig);
  b.chrome.contextMenus.onClicked.fire({ menuItemId: VIDEO, pageUrl: "https://e/watch3" }, { url: "https://e/watch3" });
  await flush();
  assert.equal(b.persist.local.menuError, undefined);
  assert.equal(b.calls.badge.at(-1), "");
});

test("unknown menu ids are ignored", async () => {
  const b = await installed({ host: hostWith(goodConfig) });
  const before = b.calls.native.length;
  b.chrome.contextMenus.onClicked.fire({ menuItemId: "something-else", linkUrl: "https://e/a.zip", pageUrl: "https://e/" }, {});
  await flush();
  assert.equal(b.calls.native.length, before);
});
