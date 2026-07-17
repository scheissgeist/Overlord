'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const ROOT = path.resolve(__dirname, '..');
const DEFAULT_URL = 'https://overlord-relay.fly.dev/';

function argValue(name, fallback = '') {
  const index = process.argv.indexOf(name);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

function readLocalBuild() {
  const source = fs.readFileSync(path.join(ROOT, 'public', 'app.js'), 'utf8');
  const match = source.match(/const\s+UI_BUILD\s*=\s*['"]([^'"]+)['"]/);
  if (!match) throw new Error('Could not read UI_BUILD from public/app.js');
  return match[1];
}

function servedBuild(source) {
  const match = source.match(/const\s+UI_BUILD\s*=\s*['"]([^'"]+)['"]/);
  return match ? match[1] : '';
}

function normalizeIndex(source) {
  return source.replace(/data-twitch-client-id="[^"]*"/, 'data-twitch-client-id=""');
}

function sha256(source) {
  return crypto.createHash('sha256').update(source, 'utf8').digest('hex');
}

function localAssets() {
  const app = fs.readFileSync(path.join(ROOT, 'public', 'app.js'), 'utf8');
  const style = fs.readFileSync(path.join(ROOT, 'public', 'style.css'), 'utf8');
  const index = normalizeIndex(fs.readFileSync(path.join(ROOT, 'public', 'index.html'), 'utf8'));
  return {
    app: sha256(app),
    style: sha256(style),
    index: sha256(index),
  };
}

async function fetchText(url) {
  const response = await fetch(url, {
    cache: 'no-store',
    signal: AbortSignal.timeout(10000),
    headers: { 'Cache-Control': 'no-cache' },
  });
  if (!response.ok) throw new Error(`${url} returned HTTP ${response.status}`);
  return { text: await response.text(), headers: response.headers, status: response.status };
}

async function observe(baseUrl, expected) {
  const healthUrl = new URL('health', baseUrl).toString();
  const appUrl = new URL('app.js', baseUrl).toString();
  const styleUrl = new URL('style.css', baseUrl).toString();
  const indexUrl = new URL('', baseUrl).toString();
  const [healthResponse, appResponse, styleResponse, indexResponse] = await Promise.all([
    fetchText(healthUrl),
    fetchText(appUrl),
    fetchText(styleUrl),
    fetchText(indexUrl),
  ]);
  const health = JSON.parse(healthResponse.text);
  const appBuild = servedBuild(appResponse.text);
  const expectedAssets = localAssets();
  const liveAssets = {
    app: sha256(appResponse.text),
    style: sha256(styleResponse.text),
    index: sha256(normalizeIndex(indexResponse.text)),
  };
  const cacheControl = {
    app: appResponse.headers.get('cache-control') || '',
    style: styleResponse.headers.get('cache-control') || '',
    index: indexResponse.headers.get('cache-control') || '',
  };
  const assetsMatch = Object.keys(expectedAssets).every(key => expectedAssets[key] === liveAssets[key]);
  const noStore = Object.values(cacheControl).every(value => /(?:^|,)\s*no-store(?:,|$)/i.test(value));
  return {
    baseUrl,
    expectedBuild: expected,
    healthOk: health.ok === true,
    healthBuild: String(health.clientBuild || ''),
    servedBuild: appBuild,
    cacheControl,
    assetsMatch,
    expectedAssets: Object.fromEntries(Object.entries(expectedAssets).map(([key, value]) => [key, value.slice(0, 12)])),
    liveAssets: Object.fromEntries(Object.entries(liveAssets).map(([key, value]) => [key, value.slice(0, 12)])),
    host: health.host === true,
    viewers: Number(health.viewers || 0),
    instance: String(health.instance || ''),
    ready: health.ok === true &&
      String(health.clientBuild || '') === expected &&
      appBuild === expected &&
      assetsMatch && noStore,
  };
}

async function main() {
  const baseUrl = new URL(argValue('--url', process.env.OVERLORD_LIVE_URL || DEFAULT_URL)).toString();
  const expected = argValue('--expected', process.env.OVERLORD_EXPECTED_BUILD || readLocalBuild());
  const timeoutMs = Math.max(5000, Number(argValue('--timeout-ms', '60000')) || 60000);
  const startedAt = Date.now();
  let last = null;
  let lastError = '';

  while (Date.now() - startedAt < timeoutMs) {
    try {
      last = await observe(baseUrl, expected);
      lastError = '';
      if (last.ready) {
        console.log(JSON.stringify({
          ok: true,
          gate: 'live-release',
          elapsedMs: Date.now() - startedAt,
          ...last,
        }, null, 2));
        return;
      }
    } catch (error) {
      lastError = error.message;
    }
    await new Promise(resolve => setTimeout(resolve, 2000));
  }

  throw new Error(JSON.stringify({
    gate: 'live-release',
    expectedBuild: expected,
    elapsedMs: Date.now() - startedAt,
    lastError,
    lastObservation: last,
  }));
}

main().catch(error => {
  let detail = error.message;
  try { detail = JSON.parse(error.message); } catch (_) {}
  console.error(JSON.stringify({ ok: false, error: detail }, null, 2));
  process.exit(1);
});
