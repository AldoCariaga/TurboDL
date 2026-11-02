// TurboDL service worker: catches browser downloads and hands them to the TurboDL app
// through the native messaging host. All state lives in chrome.storage, because MV3
// service workers are stopped and restarted at any time.
//
// chrome.storage.local:   catching (bool, default true), config, hostStatus,
//                         menuError (a failed "Download with TurboDL"; shows the red "!" badge)
// chrome.storage.session: bypass (URLs handed back to Chrome, so they aren't caught again)
"use strict";

importScripts("lib.js");

const {
  HOST_NAME,
  isHttpUrl,
  baseName,
  downloadSize,
  shouldCatch,
  buildCookieHeader,
  addBypass,
  consumeBypass,
  buildAddMessage,
  parseConfigReply,
} = self.TurboDL;

const MENU_ID = "turbodl-download-link";
const ALARM_ID = "turbodl-refresh-config";
const REFRESH_MINUTES = 10;

// ---------- storage helpers ----------

async function getLocal() {
  const s = await chrome.storage.local.get(["catching", "config", "hostStatus", "menuError"]);
  return {
    catching: s.catching !== false, // default ON
    config: s.config || null,
    hostStatus: s.hostStatus || null,
    menuError: s.menuError || null,
  };
}

async function setHostStatus(reachable, error) {
  await chrome.storage.local.set({
    hostStatus: { reachable, error: error || null, checkedAt: Date.now() },
  });
}

// Read-modify-write of the bypass list is serialised within one worker instance.
let bypassChain = Promise.resolve();
function withBypass(fn) {
  const run = bypassChain.then(async () => {
    const { bypass } = await chrome.storage.session.get("bypass");
    const { result, list } = fn(bypass || [], Date.now());
    await chrome.storage.session.set({ bypass: list });
    return result;
  });
  bypassChain = run.catch(() => {});
  return run;
}

function rememberBypass(url) {
  return withBypass((list, now) => ({ result: undefined, list: addBypass(list, url, now) }));
}

function takeBypass(urls) {
  return withBypass((list, now) => {
    const { matched, list: next } = consumeBypass(list, urls, now);
    return { result: matched, list: next };
  });
}

// ---------- native host ----------

// Sends one message to the host. Returns { reply, error }: reply is set only when the host
// answered ok:true. A successful call also clears a pending context-menu error.
async function callHost(message) {
  let reply;
  try {
    reply = await chrome.runtime.sendNativeMessage(HOST_NAME, message);
  } catch (e) {
    const error = (e && e.message) || String(e);
    await setHostStatus(false, error);
    return { reply: null, error };
  }
  if (!reply || reply.ok !== true) {
    const error = (reply && reply.error) || "TurboDL replied with an error.";
    await setHostStatus(true, error);
    return { reply: null, error };
  }
  await setHostStatus(true, null);
  const { menuError } = await chrome.storage.local.get("menuError");
  if (menuError) await chrome.storage.local.remove("menuError");
  return { reply, error: null };
}

// Fetches catchExtensions/minCatchSizeKB from the app. On failure the cached config is kept.
async function refreshConfig() {
  const { reply } = await callHost({ type: "getConfig" });
  const config = parseConfigReply(reply, Date.now());
  if (config) await chrome.storage.local.set({ config });
  return config;
}

async function cookieHeader(url) {
  try {
    return buildCookieHeader(await chrome.cookies.getAll({ url }));
  } catch (e) {
    console.warn("TurboDL: could not read cookies", e);
    return "";
  }
}

// Gives a download back to Chrome, marked so onCreated lets it through.
async function downloadInChrome(url) {
  await rememberBypass(url);
  try {
    await chrome.downloads.download({ url });
  } catch (e) {
    console.error("TurboDL: Chrome fallback download failed", e);
  }
}

// ---------- catching ----------

async function handleCreated(item) {
  // Our own fallback downloads: let them through and use up their bypass entry.
  if (item.byExtensionId && item.byExtensionId === chrome.runtime.id) {
    await takeBypass([item.url, item.finalUrl]);
    return;
  }
  if (item.state && item.state !== "in_progress") return;

  const url = item.finalUrl || item.url;
  if (!isHttpUrl(url)) return; // blob:, data:, file:, ftp:, chrome: ... stay in Chrome
  if (await takeBypass([item.url, item.finalUrl])) return;

  const { catching, config } = await getLocal();
  if (!catching || !shouldCatch(item, config)) return;

  try {
    await chrome.downloads.cancel(item.id);
  } catch (e) {
    console.warn("TurboDL: cancel failed", e);
  }
  try {
    await chrome.downloads.erase({ id: item.id });
  } catch (e) {
    console.warn("TurboDL: erase failed", e);
  }

  const message = buildAddMessage({
    url,
    // Only Chrome's final name; when it isn't known yet the app works it out from the server.
    filename: baseName(item.filename),
    referrer: item.referrer,
    userAgent: navigator.userAgent,
    cookies: await cookieHeader(url),
    size: downloadSize(item),
  });

  // Caught downloads go back to Chrome when TurboDL can't take them.
  const { reply } = await callHost(message);
  if (!reply) await downloadInChrome(item.url || url);
}

chrome.downloads.onCreated.addListener((item) => {
  handleCreated(item).catch((e) => console.error("TurboDL: onCreated failed", e));
});

// ---------- context menu ----------

function createMenu() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: "Download with TurboDL",
      contexts: ["link"],
      targetUrlPatterns: ["http://*/*", "https://*/*"],
    });
  });
}

async function handleMenuClick(info, tab) {
  if (info.menuItemId !== MENU_ID) return;
  const url = info.linkUrl;
  if (!isHttpUrl(url)) return;
  const message = buildAddMessage({
    url,
    referrer: info.frameUrl || info.pageUrl || (tab && tab.url) || "",
    userAgent: navigator.userAgent,
    cookies: await cookieHeader(url),
  });
  // The user explicitly chose TurboDL here, so a failure is reported, not sent to Chrome.
  const { reply, error } = await callHost(message);
  if (!reply) {
    await chrome.storage.local.set({ menuError: { url, error, at: Date.now() } });
  }
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  handleMenuClick(info, tab).catch((e) => console.error("TurboDL: menu click failed", e));
});

// ---------- badge ----------

// Red "!" while a context-menu send has failed and hasn't been seen; grey "off" when catching is off.
async function updateBadge() {
  const { catching, menuError } = await getLocal();
  let color = "#6b6b6b";
  let text = "";
  let title = "TurboDL: catching downloads";
  if (menuError) {
    color = "#c42b1c";
    text = "!";
    title = "TurboDL: couldn't send a link to TurboDL. Click for details.";
  } else if (!catching) {
    text = "off";
    title = "TurboDL: catching is off";
  }
  await chrome.action.setBadgeBackgroundColor({ color });
  await chrome.action.setBadgeText({ text });
  await chrome.action.setTitle({ title });
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && ("catching" in changes || "menuError" in changes)) {
    updateBadge().catch((e) => console.error("TurboDL: badge update failed", e));
  }
});

// ---------- lifecycle ----------

async function ensureAlarm() {
  const existing = await chrome.alarms.get(ALARM_ID);
  if (!existing) {
    await chrome.alarms.create(ALARM_ID, { periodInMinutes: REFRESH_MINUTES });
  }
}

async function startUp() {
  await ensureAlarm();
  await updateBadge();
  await refreshConfig();
}

chrome.runtime.onInstalled.addListener(() => {
  createMenu();
  startUp().catch((e) => console.error("TurboDL: install setup failed", e));
});

chrome.runtime.onStartup.addListener(() => {
  startUp().catch((e) => console.error("TurboDL: startup failed", e));
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name !== ALARM_ID) return;
  refreshConfig().catch((e) => console.error("TurboDL: config refresh failed", e));
});

// The popup asks for a fresh check with { type: "refreshConfig" }.
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || !message || message.type !== "refreshConfig") return false;
  refreshConfig()
    .then((config) => sendResponse({ ok: !!config }))
    .catch((e) => sendResponse({ ok: false, error: String(e) }));
  return true; // keep the channel open for the async reply
});
