'use strict';

const childProcess = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const REPO = path.resolve(ROOT, '..');
const DEPLOY = process.argv.includes('--deploy');
const startedAt = Date.now();
const completed = [];

function executable(name) {
  return process.platform === 'win32' && name === 'npm' ? 'npm.cmd' : name;
}

function run(step, command, args, options = {}) {
  process.stdout.write(`[release] ${step}\n`);
  const started = Date.now();
  const result = childProcess.spawnSync(executable(command), args, {
    cwd: options.cwd || ROOT,
    env: { ...process.env, ...(options.env || {}) },
    encoding: 'utf8',
    stdio: 'inherit',
    timeout: options.timeout || 120000,
    windowsHide: true,
  });
  if (result.error) throw new Error(`${step}: ${result.error.message}`);
  if (result.status !== 0) throw new Error(`${step}: exited ${result.status}`);
  completed.push({ step, elapsedMs: Date.now() - started });
}

function readBuild() {
  const source = fs.readFileSync(path.join(ROOT, 'public', 'app.js'), 'utf8');
  const match = source.match(/const\s+UI_BUILD\s*=\s*['"]([^'"]+)['"]/);
  if (!match) throw new Error('Could not read UI_BUILD from public/app.js');
  return match[1];
}

function verifyScreenshots() {
  const directory = path.join(REPO, 'output', 'playwright');
  const required = [
    'overlord-gear-desktop.png',
    'overlord-armory-desktop.png',
    'overlord-compact-chrome.png',
    'overlord-gear-narrow.png',
  ];
  const missing = required.filter(file => {
    const fullPath = path.join(directory, file);
    return !fs.existsSync(fullPath) || fs.statSync(fullPath).size === 0;
  });
  if (missing.length) throw new Error(`Visual artifacts missing: ${missing.join(', ')}`);
  completed.push({ step: 'visual artifacts', files: required });
}

function main() {
  const expectedBuild = readBuild();
  run('C# release build', 'dotnet', ['build', path.join(REPO, 'Overlord.csproj'), '-c', 'Release']);
  run('viewer syntax', 'node', ['--check', path.join(ROOT, 'public', 'app.js')]);
  run('relay syntax', 'node', ['--check', path.join(ROOT, 'server.js')]);
  run('smoke syntax', 'node', ['--check', path.join(ROOT, 'scripts', 'smoke-viewer-intake.js')]);
  run('live verifier syntax', 'node', ['--check', path.join(ROOT, 'scripts', 'verify-live-release.js')]);
  run('whitespace gate', 'git', ['diff', '--check'], { cwd: REPO });
  run('fresh viewer runtime smoke', 'node', [path.join(ROOT, 'scripts', 'smoke-viewer-intake.js')], {
    env: { OVERLORD_SMOKE_SCREENSHOTS: '1', OVERLORD_SMOKE_SUMMARY: '1' },
  });
  verifyScreenshots();

  if (DEPLOY) {
    run('install both local mod copies', 'powershell.exe', [
      '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
      path.join(REPO, 'scripts', 'deploy-mod.ps1'), '-SkipBuild', '-Prune',
    ], { cwd: REPO });
    run('Fly rolling deploy', 'powershell.exe', [
      '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
      path.join(ROOT, 'scripts', 'fly.ps1'), 'deploy',
    ], { timeout: 240000 });
    run('live build verification', 'node', [
      path.join(ROOT, 'scripts', 'verify-live-release.js'),
      '--expected', expectedBuild,
    ], { timeout: 90000 });
  }

  console.log(JSON.stringify({
    ok: true,
    gate: DEPLOY ? 'release-fly' : 'release-local',
    expectedBuild,
    elapsedMs: Date.now() - startedAt,
    completed,
  }, null, 2));
}

try {
  main();
} catch (error) {
  console.error(JSON.stringify({
    ok: false,
    gate: DEPLOY ? 'release-fly' : 'release-local',
    elapsedMs: Date.now() - startedAt,
    completed,
    error: error.message,
  }, null, 2));
  process.exit(1);
}
