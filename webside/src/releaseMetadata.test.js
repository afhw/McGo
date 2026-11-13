import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { loadReleaseMetadata, parseReleaseMetadata } from './releaseMetadata.js';

const manifestUrl = 'https://mcgo.flyliq.cn/update-csharp.json';
const manifest = JSON.parse((await readFile(new URL('../public/update-csharp.json', import.meta.url), 'utf8')).replace(/^\uFEFF/, ''));

test('website release values come from the same updater manifest', () => {
  const release = parseReleaseMetadata(manifest, manifestUrl, { installer: true });
  assert.equal(release.version, manifest.version);
  assert.equal(release.bytes, manifest.size);
  assert.equal(release.sha256, manifest.sha256);
  assert.equal(release.href, new URL(manifest.url, manifestUrl).href);
  assert.equal(release.size, `${(manifest.size / 1048576).toFixed(1)} MiB`);
  assert.equal(release.checksumHref, `${release.href}.sha256`);
});

test('changed releases and filenames do not need frontend edits', () => {
  const release = parseReleaseMetadata({ ...manifest, version: '9.8.7', size: 1048576, url: 'https://cdn.example/New.exe?download=1', sha256: 'a'.repeat(64) }, manifestUrl);
  assert.equal(release.version, '9.8.7');
  assert.equal(release.size, '1.0 MiB');
  assert.equal(release.name, 'New.exe');
  assert.equal(release.sha256, 'a'.repeat(64));
  assert.equal(release.checksumHref, 'https://cdn.example/New.exe.sha256?download=1');
});

test('legacy manifests remain usable without a GitHub fallback', () => {
  const legacy = { ...manifest };
  delete legacy.github_url;
  const release = parseReleaseMetadata(legacy, manifestUrl, { installer: true });
  assert.equal(release.href, new URL(legacy.url, manifestUrl).href);
  assert.equal(Object.hasOwn(release, 'githubHref'), false);
});

test('GitHub release fallbacks preserve the tracked primary download for both editions', () => {
  for (const [filename, installer] of [['McGo-CSharp-Preview-x64-Setup.exe', true], ['McGo.exe', false], ['McGo Setup.exe', true]]) {
    const asset = encodeURIComponent(filename);
    const githubUrl = `https://github.com/afhw/McGo/releases/download/v1.1.3/${asset}`;
    const release = parseReleaseMetadata({ ...manifest, installer, url: `/download/${asset}`, github_url: githubUrl }, manifestUrl, { installer });
    assert.equal(release.githubHref, githubUrl);
    assert.equal(release.href, `https://mcgo.flyliq.cn/download/${asset}`);
    assert.equal(release.checksumHref, `${release.href}.sha256`);
    assert.equal(release.name, filename);
    assert.equal(release.sha256, manifest.sha256);
    assert.equal(release.bytes, manifest.size);
  }
});

test('invalid GitHub fallbacks are ignored without disabling or changing the primary download', () => {
  const base = { ...manifest, url: '/download/McGo.exe', github_url: undefined };
  const expected = parseReleaseMetadata(base, manifestUrl, { installer: true });
  const assetPath = '/afhw/McGo/releases/download/v1.1.3/McGo.exe';
  for (const github_url of [
    undefined, null, '', false, 123, {}, 'not a URL',
    `http://github.com${assetPath}`, `//github.com${assetPath}`,
    'javascript:alert(1)', 'data:text/html,<script>alert(1)</script>',
    `https://github.com.evil.example${assetPath}`, `https://evil.example${assetPath}`,
    `https://github.com@evil.example${assetPath}`, `https://user:pass@github.com${assetPath}`,
    `https://github.com:8443${assetPath}`,
    `https://github.com${assetPath}?redirect=https://evil.example`, `https://github.com${assetPath}#download`,
    'https://github.com/afhw/McGo/releases/tag/v1.1.3',
    'https://github.com/afhw/McGo/releases/latest/download/McGo.exe',
    'https://github.com/afhw/McGo/releases/download/v1.1.3/Other.exe',
    'https://github.com/afhw/McGo/releases/download/v1.1.3/mcgo.exe',
    'https://github.com/afhw/McGo/releases/download/v1.1.3/subdir/McGo.exe',
    'https://github.com/afhw/McGo/releases/download/v1.1.3/%E0%A4%A',
  ]) {
    assert.deepEqual(parseReleaseMetadata({ ...base, github_url }, manifestUrl, { installer: true }), expected, String(github_url));
  }
});

test('invalid or non-installer metadata cannot enable the preview download', () => {
  for (const change of [{ version: '' }, { size: -1 }, { size: '123' }, { sha256: 'unknown' }, { url: '' }, { url: 'javascript:alert(1)' }, { installer: false }, { product: 'other' }]) {
    assert.throws(() => parseReleaseMetadata({ ...manifest, ...change }, manifestUrl, { installer: true }));
  }
});

test('release loading bypasses stale HTTP caches and propagates failures', async () => {
  const signal = new AbortController().signal;
  const release = await loadReleaseMetadata('/update-csharp.json', { installer: true, signal }, async (path, options) => {
    assert.equal(path, '/update-csharp.json');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.signal, signal);
    return { ok: true, url: manifestUrl, json: async () => manifest };
  });
  assert.equal(release.version, manifest.version);
  await assert.rejects(loadReleaseMetadata('/update-csharp.json', {}, async () => ({ ok: false, status: 503 })), /503/);
  await assert.rejects(loadReleaseMetadata('/update-csharp.json', {}, async () => { throw new Error('offline'); }), /offline/);
});
