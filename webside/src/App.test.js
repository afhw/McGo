import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

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
  } finally {
    await server.close();
  }
});
