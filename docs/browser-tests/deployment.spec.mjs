import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname } from 'node:path';
import { expect, test } from '@playwright/test';

const site = new URL('../dist/', import.meta.url);
const manifest = JSON.parse(await readFile(new URL('try/asset-manifest.json', site), 'utf8'));
const currentBase = `/try/${manifest.directory}/`;
const contentTypes = {
  '.html': 'text/html', '.js': 'application/javascript', '.json': 'application/json', '.wasm': 'application/wasm',
  '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2',
};

async function serveDeployment(previousBase) {
  let deployed = false;
  const requests = [];
  const server = createServer(async (request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    requests.push({ deployed, path });
    const asset = path.startsWith(previousBase) && path !== '/try/';
    const mapped = !deployed && asset ? currentBase + path.slice(previousBase.length) : path;
    const file = new URL('.' + mapped + (mapped.endsWith('/') ? 'index.html' : ''), site);
    try {
      let body = await readFile(file);
      if (!deployed && path === '/try/') body = Buffer.from(body.toString().replaceAll(currentBase, previousBase));
      response.setHeader('Content-Type', contentTypes[extname(file.pathname)] || 'application/octet-stream');
      // Use a real HTTP cache. Request routing disables it and would hide stale worker imports.
      response.setHeader('Cache-Control', extname(file.pathname) === '.html' ? 'no-cache' : 'public, max-age=600');
      response.end(body);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return {
    url: `http://127.0.0.1:${server.address().port}/try/`, requests,
    deploy: () => { deployed = true; },
    close: () => new Promise(resolve => server.close(resolve)),
  };
}

async function ready(page, count = 1) {
  await expect.poll(() => page.evaluate(() => window.ilreplSessionCount)).toBe(count);
  await expect(page.locator('#session-status')).toHaveText('Ready');
  await expect(page.locator('#terminal')).toContainText('il[1]>');
}

async function restart(page) {
  const count = await page.evaluate(() => window.ilreplSessionCount);
  await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
  await ready(page, count + 1);
}

async function history(page) {
  return page.evaluate(() => new Promise((resolve, reject) => {
    const opened = indexedDB.open('ilrepl', 1);
    opened.onerror = () => reject(opened.error);
    opened.onsuccess = () => {
      const db = opened.result;
      const request = db.transaction('history', 'readonly').objectStore('history').getAll();
      request.onsuccess = () => { db.close(); resolve(request.result); };
      request.onerror = () => { db.close(); reject(request.error); };
    };
  }));
}

for (const previousBase of ['/try/', '/try/assets/previous/']) {
  test(`deployment replaces cached worker imports from ${previousBase} without clearing history`, async ({ page, context }) => {
    const deployment = await serveDeployment(previousBase);
    try {
      await page.goto(deployment.url);
      await ready(page);
      const bootPath = previousBase + '_framework/dotnet.js';
      expect(deployment.requests.filter(request => request.path === bootPath)).toHaveLength(1);
      await restart(page);
      // A second real worker reads its boot module from the warm cache.
      expect(deployment.requests.filter(request => request.path === bootPath)).toHaveLength(1);

      await page.locator('#terminal .xterm-helper-textarea').focus();
      await page.keyboard.type('ldc.i4.s 37');
      await page.keyboard.press('Enter');
      await expect.poll(() => history(page)).toContain('ldc.i4.s 37');
      const savedHistory = await history(page);

      deployment.deploy();
      const client = await context.newCDPSession(page);
      const loaded = page.waitForEvent('load');
      await client.send('Page.reload', { ignoreCache: true });
      await loaded;
      await ready(page);
      expect(await history(page)).toEqual(savedHistory);
      const worker = page.workers().find(worker => worker.url().endsWith('/worker.js'));
      expect(new URL(worker.url()).pathname).toBe(currentBase + 'worker.js');
      for (const name of ['main.js', 'worker.js', '_framework/dotnet.js', 'interop.js', 'samples/Greeter.dll']) {
        expect(deployment.requests.filter(request => request.deployed && request.path === currentBase + name)).toHaveLength(1);
      }
      expect(deployment.requests.filter(request => request.deployed && request.path === bootPath)).toEqual([]);

      const initial = await page.locator('#terminal').innerText();
      await restart(page);
      await expect(page.locator('#terminal')).not.toContainText('Session opened. Nothing has run yet.');
      await expect(page.locator('#terminal')).toHaveText(initial, { useInnerText: true });
      expect(await history(page)).toEqual(savedHistory);
      expect(deployment.requests.filter(request => request.deployed
        && request.path === currentBase + '_framework/dotnet.js')).toHaveLength(1);
    } finally {
      await page.close();
      await deployment.close();
    }
  });
}
