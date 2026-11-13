import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

test('download redirects use the exact GitHub assets declared in both release manifests', async () => {
  const config = await readFile(new URL('../deploy/download-routes.conf', import.meta.url), 'utf8');
  const routes = new Map([...config.matchAll(/location\s*=\s*(\S+)\s*\{([^}]+)\}/g)]
    .map(([, path, body]) => [path, body]));
  for (const filename of ['update-csharp.json', 'update.json']) {
    const manifest = JSON.parse(await readFile(new URL(`../public/${filename}`, import.meta.url), 'utf8'));
    assert.match(manifest.url, /^\/download\/[^/?#]+\.exe$/);
    assert.match(manifest.github_url, /^https:\/\/github\.com\/afhw\/McGo\/releases\/download\/[^/]+\/[^/]+\.exe$/);
    assert.equal(manifest.url.split('/').at(-1), manifest.github_url.split('/').at(-1));
    const paths = filename === 'update-csharp.json' ? ['', '.sha256'] : [''];
    for (const suffix of paths) {
      const route = routes.get(manifest.url + suffix);
      assert.ok(route, `Missing redirect for ${manifest.url}${suffix}`);
      assert.match(route, /add_header Cache-Control "no-store" always;/);
      const target = route.match(/return 302 (\S+);/);
      assert.equal(target?.[1], `https://gh-proxy.org/${manifest.github_url}${suffix}`);
    }
  }
});
