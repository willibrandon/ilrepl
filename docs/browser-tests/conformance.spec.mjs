import { readFile } from 'node:fs/promises';
import { expect, test } from '@playwright/test';

const corpus = JSON.parse(await readFile(new URL('../../artifacts/browser-corpus.json', import.meta.url), 'utf8'));
const assets = JSON.parse(await readFile(new URL('../public/try/asset-manifest.json', import.meta.url), 'utf8'));
const workerUrl = `/try/${assets.directory}/conformance-worker.js`;
if (!Array.isArray(corpus) || corpus.length === 0) throw new Error('The browser conformance corpus is empty.');

for (const fixture of corpus) {
  test(`${fixture.name} agrees across independent and exported images on Mono WebAssembly`, async ({ page }) => {
    await page.goto('/');
    expect(fixture.arguments).toEqual([]);
    expect(fixture.genericArguments).toEqual([]);
    expect(fixture.images.map(image => image.kind).sort()).toEqual(['ilasm', 'ildasm', 'independent', 'saved']);
    for (const artifact of fixture.images) {
      const observed = await page.evaluate(({ workerUrl, fixture, artifact }) => new Promise((resolve, reject) => {
        const worker = new Worker(workerUrl, { type: 'module' });
        const timeout = setTimeout(() => {
          worker.terminate();
          reject(new Error('The isolated conformance image did not finish within 30 seconds.'));
        }, 30_000);
        const finish = () => { clearTimeout(timeout); worker.terminate(); };
        worker.onmessage = ({ data }) => {
          finish();
          if (data.error) reject(new Error(data.error + '\n' + (data.stack || '')));
          else resolve(data.result);
        };
        worker.onerror = error => { finish(); reject(new Error(error.message)); };
        worker.postMessage({ image: artifact.image, type: fixture.type, method: fixture.method });
      }), { workerUrl, fixture, artifact });
      expect(observed, `${fixture.name}: ${artifact.kind}`).toBe(fixture.expected);
    }
  });
}
