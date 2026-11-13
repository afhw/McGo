import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';

test('homepage renders before either download manifest or statistics have loaded', async () => {
  const server = await createServer({
    root: fileURLToPath(new URL('..', import.meta.url)),
    configFile: false,
    optimizeDeps: { noDiscovery: true, include: [] },
    server: { middlewareMode: true, hmr: false, watch: null },
    appType: 'custom',
  });
  try {
    const { default: App } = await server.ssrLoadModule('/src/App.jsx');
    const html = renderToStaticMarkup(React.createElement(App));
    assert.match(html, /<h1 id="hero-title">McGo<\/h1>/);
    assert.match(html, /id="download"/);
    assert.match(html, /id="gallery"/);
    assert.match(html, /正在获取大小/);
    assert.equal((html.match(/<button[^>]+disabled=""/g) || []).length, 4);
    assert.doesNotMatch(html, /undefined|null/);
    const capture = JSON.parse(await readFile(new URL('./screenshotSet.json', import.meta.url), 'utf8'));
    const imageUrls = [...html.matchAll(/(?:src|srcSet)="([^"]*\/screenshots\/[^" ]+)"/g)]
      .map(([, url]) => url);
    assert.equal(imageUrls.length, 6);
    for (const variant of Object.values(capture.variants)) {
      const url = `${capture.prefix}/${variant.directory}/01-home.png`;
      assert.equal(imageUrls.filter(imageUrl => imageUrl === url).length, 2);
    }
  } finally {
    await server.close();
  }
});

test('gallery screenshots match their recorded native dimensions and hashes', async () => {
  const capture = JSON.parse(await readFile(new URL('./screenshotSet.json', import.meta.url), 'utf8'));
  assert.equal(capture.files.length, 18);
  assert.equal(Object.keys(capture.variants).length, 3);
  const published = JSON.parse(await readFile(new URL(`../public${capture.prefix}/manifest.json`, import.meta.url), 'utf8'));
  assert.deepEqual(published, capture);
  const filenames = ['01-home.png', '02-launch.png', '03-download.png', '04-resources.png', '05-accounts.png', '06-online.png'];
  const expectedUrls = Object.values(capture.variants).flatMap(variant =>
    filenames.map(file => `${capture.prefix}/${variant.directory}/${file}`));
  assert.deepEqual(capture.files.map(image => image.url).sort(), expectedUrls.sort());
  for (const image of capture.files) {
    const bytes = await readFile(new URL(`../public${image.url}`, import.meta.url));
    assert.equal(bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
    assert.equal(bytes.readUInt32BE(16), image.width);
    assert.equal(bytes.readUInt32BE(20), image.height);
    assert.equal(createHash('sha256').update(bytes).digest('hex'), image.sha256);
  }
  for (const variant of Object.values(capture.variants)) {
    assert.equal(capture.files.filter(image => image.width === variant.width && image.height === variant.height).length, 6);
  }
});
