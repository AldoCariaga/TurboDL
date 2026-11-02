"use strict";

const $ = (id) => document.getElementById(id);

function ago(ts) {
  if (!ts) return "";
  const s = Math.max(0, Math.round((Date.now() - ts) / 1000));
  if (s < 60) return "just now";
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} h ago`;
  return new Date(ts).toLocaleString();
}

function render({ catching, config, hostStatus }) {
  const on = catching !== false;
  $("catching").checked = on;
  $("catch-hint").textContent = on
    ? "Matching downloads open in TurboDL."
    : "Off: Chrome keeps all downloads.";

  const dot = $("dot");
  dot.className = "dot";
  const err = $("host-error");
  err.hidden = true;
  if (!hostStatus) {
    $("host-line").textContent = "Not checked yet";
  } else if (!hostStatus.reachable) {
    dot.classList.add("bad");
    $("host-line").textContent = `Can't reach TurboDL (${ago(hostStatus.checkedAt)})`;
    if (hostStatus.error) {
      err.textContent = hostStatus.error;
      err.hidden = false;
    }
  } else if (hostStatus.error) {
    dot.classList.add("bad");
    $("host-line").textContent = `TurboDL replied with an error (${ago(hostStatus.checkedAt)})`;
    err.textContent = hostStatus.error;
    err.hidden = false;
  } else {
    dot.classList.add("ok");
    $("host-line").textContent = `Connected to TurboDL (${ago(hostStatus.checkedAt)})`;
  }

  if (config && config.fetchedAt) {
    const n = Array.isArray(config.catchExtensions) ? config.catchExtensions.length : 0;
    $("config-line").textContent =
      `Settings updated ${ago(config.fetchedAt)} · ${n} file type${n === 1 ? "" : "s"}`;
  } else {
    $("config-line").textContent = "No settings from TurboDL yet, so nothing is caught";
  }
}

// A failed "Download with TurboDL" stays visible while this popup is open, but is marked as seen
// (removed from storage) as soon as it is shown, which clears the red badge.
let shownMenuError = null;

function renderMenuError() {
  const box = $("menu-error");
  if (!shownMenuError) {
    box.hidden = true;
    return;
  }
  $("menu-error-url").textContent = shownMenuError.url || "";
  $("menu-error-text").textContent =
    `${shownMenuError.error || "Unknown error"} (${ago(shownMenuError.at)})`;
  box.hidden = false;
}

async function load() {
  const state = await chrome.storage.local.get(["catching", "config", "hostStatus", "menuError"]);
  if (state.menuError) {
    shownMenuError = state.menuError;
    await chrome.storage.local.remove("menuError");
  }
  render(state);
  renderMenuError();
}

$("catching").addEventListener("change", (e) => {
  chrome.storage.local.set({ catching: e.target.checked });
});

$("check").addEventListener("click", async () => {
  const btn = $("check");
  btn.disabled = true;
  btn.textContent = "Checking…";
  try {
    await chrome.runtime.sendMessage({ type: "refreshConfig" });
  } catch (e) {
    console.error("TurboDL: refresh failed", e);
  } finally {
    btn.disabled = false;
    btn.textContent = "Check now";
    await load();
  }
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local") load();
});

load();
