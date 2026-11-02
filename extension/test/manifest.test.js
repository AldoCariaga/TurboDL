// Run with: node extension/test/manifest.test.js
// Checks the manifest and that EXTENSION_ID.txt matches the public key in it.
//
// Chrome's extension ID = SHA-256 of the DER-encoded public key (SubjectPublicKeyInfo, which is
// exactly what the base64 `key` holds), first 16 bytes as 32 hex digits, each digit 0-f mapped to a-p.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "..");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8"));

function extensionIdFromKey(base64Key) {
  const der = Buffer.from(base64Key, "base64");
  const hex = crypto.createHash("sha256").update(der).digest("hex").slice(0, 32);
  return [...hex].map((c) => String.fromCharCode("a".charCodeAt(0) + parseInt(c, 16))).join("");
}

test("manifest has the MV3 fields", () => {
  assert.equal(manifest.manifest_version, 3);
  assert.equal(typeof manifest.name, "string");
  assert.match(manifest.version, /^\d+(\.\d+){0,3}$/);
  assert.equal(manifest.background.service_worker, "background.js");
  assert.equal(manifest.background.scripts, undefined, "no MV2 background pages");
  for (const p of ["downloads", "cookies", "nativeMessaging", "contextMenus", "storage", "alarms"]) {
    assert.ok(manifest.permissions.includes(p), `permission ${p}`);
  }
  assert.deepEqual(manifest.host_permissions, ["<all_urls>"]);
  assert.equal(manifest.action.default_popup, "popup.html");
  assert.equal(manifest.options_page, undefined, "no options page");
  assert.equal(manifest.options_ui, undefined, "no options page");
});

test("every file the manifest points at exists", () => {
  const files = [
    manifest.background.service_worker,
    manifest.action.default_popup,
    ...Object.values(manifest.icons),
    ...Object.values(manifest.action.default_icon),
  ];
  for (const f of files) assert.ok(fs.existsSync(path.join(root, f)), f);
  for (const [size, f] of Object.entries(manifest.icons)) {
    const png = fs.readFileSync(path.join(root, f));
    assert.equal(png.toString("hex", 0, 8), "89504e470d0a1a0a", `${f} is a PNG`);
    assert.equal(png.readUInt32BE(16), Number(size), `${f} width`);
    assert.equal(png.readUInt32BE(20), Number(size), `${f} height`);
  }
});

test("key is a 2048-bit RSA public key and holds no private key", () => {
  assert.equal(typeof manifest.key, "string");
  const key = crypto.createPublicKey({ key: Buffer.from(manifest.key, "base64"), format: "der", type: "spki" });
  assert.equal(key.asymmetricKeyType, "rsa");
  assert.equal(key.asymmetricKeyDetails.modulusLength, 2048);
  const all = fs.readdirSync(root, { recursive: true }).map(String);
  for (const f of all) {
    const p = path.join(root, f);
    if (!fs.statSync(p).isFile() || /\.png$/.test(f)) continue;
    assert.ok(!/PRIVATE KEY/.test(fs.readFileSync(p, "utf8")) || f.endsWith("manifest.test.js"), `${f} must not contain a private key`);
  }
});

test("EXTENSION_ID.txt matches the key", () => {
  const fromFile = fs.readFileSync(path.join(root, "EXTENSION_ID.txt"), "utf8").trim();
  assert.match(fromFile, /^[a-p]{32}$/);
  assert.equal(extensionIdFromKey(manifest.key), fromFile);
});

test("no inline scripts in the popup (MV3 CSP)", () => {
  const html = fs.readFileSync(path.join(root, "popup.html"), "utf8");
  for (const tag of html.match(/<script\b[^>]*>[\s\S]*?<\/script>/g) || []) {
    assert.match(tag, /^<script src="[^"]+"><\/script>$/, tag);
  }
  assert.doesNotMatch(html, /\son[a-z]+=/i, "no inline event handlers");
});
