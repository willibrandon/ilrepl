import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { gzipSync, gunzipSync } from 'node:zlib';
import { expect, test } from '@playwright/test';

const effect = 'SESSION_SMOKE_EXECUTED';
const openNotice = 'Session opened. Nothing has run yet.';
const browserAssets = JSON.parse(await readFile(new URL('../public/try/asset-manifest.json', import.meta.url), 'utf8'));
const sampleUrl = new URL(`../public/try/${browserAssets.directory}/samples/Greeter.dll`, import.meta.url);

function document(lines, draft = []) {
  return {
    format: 'ilrepl-session',
    version: 1,
    runtime: {
      ilreplVersion: 'smoke', framework: 'net10.0', description: '.NET 10',
      rid: 'browser-wasm', operatingSystem: 'browser', architecture: 'wasm', culture: '',
    },
    cells: [],
    interruptions: [],
    references: [],
    assets: [],
    entries: lines.map(line => ({ identity: randomUUID(), number: 1, kind: 'source', source: [line] })),
    editor: { lines: draft, caret: draft.join('\n').length, anchor: draft.join('\n').length, revision: 1 },
  };
}

function experiment() {
  return document([
    `ldstr "${effect}"`,
    'call void [System.Console]System.Console::WriteLine(string)',
    'ldc.i4.s 42',
  ], ['// unsent Ω source']);
}

async function embeddedExperiment() {
  const image = await readFile(sampleUrl);
  const hash = createHash('sha256').update(image).digest('hex');
  const identity = randomUUID();
  const source = document([
    `ldstr "${effect}"`,
    'call void [System.Console]System.Console::WriteLine(string)',
    'ldc.i4.s 19', 'ldc.i4.s 23', 'call int32 [Greeter]Greeter.Hello::Add(int32, int32)',
  ], ['// unsent Ω source']);
  source.references = [{
    identity, origin: 'assembly', request: '/unavailable/Greeter.dll', dependencies: [], frameworks: [],
    assets: [{ name: 'Greeter', hash, path: '/unavailable/Greeter.dll', kind: 'managed' }],
  }];
  source.assets = [{ hash, image: image.toString('base64') }];
  source.entries.unshift({
    identity: randomUUID(), number: 1, kind: 'reference', reference: identity, source: ['.load /unavailable/Greeter.dll'],
  });
  return source;
}

async function ready(page, count = 1) {
  await expect.poll(() => page.evaluate(() => window.ilreplSessionCount)).toBeGreaterThanOrEqual(count);
  await expect(page.locator('#session-status')).toHaveText('Ready');
  await expect(page.locator('#terminal')).toContainText('il[1]>');
}

async function open(page, source, filename = 'experiment.ilrepl.json') {
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await (await chooser).setFiles({ name: filename, mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(source)) });
  await ready(page, count + 1);
}

async function download(page, action = () => page.getByRole('button', { name: 'Download', exact: true }).click()) {
  const downloading = page.waitForEvent('download');
  await action();
  const result = await downloading;
  expect(result.suggestedFilename()).toMatch(/\.ilrepl\.json$/i);
  const path = await result.path();
  expect(path).not.toBeNull();
  return JSON.parse(await readFile(path, 'utf8'));
}

async function focus(page) {
  await page.locator('#terminal .xterm-helper-textarea').focus();
}

async function typeLine(page, line) {
  await focus(page);
  await page.keyboard.type(line);
  await page.keyboard.press('Enter');
}

async function terminalText(page) {
  return page.evaluate(() => {
    const buffer = window.ilreplTerminal.buffer.active;
    return Array.from({ length: buffer.length }, (_, index) => buffer.getLine(index)?.translateToString(true).trim()).join(' ');
  });
}

async function outputCount(page, text) {
  return page.evaluate(expected => {
    const buffer = window.ilreplTerminal.buffer.active;
    return Array.from({ length: buffer.length }, (_, index) => buffer.getLine(index)?.translateToString(true).trim())
      .filter(line => line === expected).length;
  }, text);
}

test.beforeEach(async ({ page }) => {
  await page.goto('/try/');
  await ready(page);
});

test.afterEach(async ({ page }, testInfo) => {
  if (testInfo.status !== testInfo.expectedStatus) {
    const state = await page.evaluate(() => ({
      status: document.getElementById('session-status')?.textContent,
      message: document.getElementById('session-file-message')?.textContent,
      ready: window.ilreplReady,
      sessions: window.ilreplSessionCount,
      restart: window.ilreplLastRestart,
      terminal: document.getElementById('terminal')?.innerText,
    }));
    await testInfo.attach('workspace-state', { body: JSON.stringify(state, null, 2), contentType: 'application/json' });
  }
});

test('open and download preserve source; Run all executes only on request', async ({ page }) => {
  await typeLine(page, '.quiet on');
  await expect(page.locator('#terminal')).toContainText('stack echo off');
  await typeLine(page, '.time on');
  await expect(page.locator('#terminal')).toContainText('timing on');
  const source = await embeddedExperiment();
  await open(page, source);
  await expect(page.locator('#terminal')).toContainText(openNotice);
  await expect(page.locator('#terminal')).toContainText('// unsent Ω source');
  await expect(page.locator('#terminal')).toContainText(`il[1]> ldstr "${effect}"`);
  expect(await outputCount(page, effect)).toBe(0);
  await expect(page.locator('#terminal')).not.toContainText('= 42 : int32');

  const saved = await download(page);
  expect(saved.format).toBe('ilrepl-session');
  expect(saved.entries.map(entry => entry.source)).toEqual(source.entries.map(entry => entry.source));
  expect(saved.editor.lines).toEqual(['// unsent Ω source']);
  expect(saved.cells).toEqual([]);
  expect(saved.references).toEqual(source.references);
  expect(saved.assets).toEqual(source.assets);
  expect(saved.entries.flatMap(entry => entry.source)).not.toContain('.quiet on');
  expect(saved.entries.flatMap(entry => entry.source)).not.toContain('.time on');
  await open(page, saved);
  expect(await outputCount(page, effect)).toBe(0);

  const count = await page.evaluate(() => window.ilreplSessionCount);
  await page.getByRole('button', { name: 'Run all', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
  await expect.poll(() => outputCount(page, effect)).toBe(1);
  await expect(page.locator('#terminal')).toContainText(/= 42 : int32\s+[\d.]+ (?:ms|µs)/);
  await expect(page.locator('#terminal')).not.toContainText('┊');
  const executed = await download(page);
  expect(executed.cells).toHaveLength(1);
  expect(executed.cells[0].state).toBe('succeeded');
  expect(executed.cells[0].output.map(line => line.spans.map(span => span.text).join('')).join('\n')).toContain('= 42 : int32');
  expect(executed.assets).toEqual(source.assets);
  await open(page, executed);
  await expect(page.locator('#terminal')).toContainText('1: cell, succeeded (historical)');
  await expect(page.locator('#terminal')).toContainText('= 42 : int32');
  await expect(page.locator('#terminal')).toContainText('end of saved history; no code executed');
  expect(await outputCount(page, effect)).toBe(1);
  const reopened = await download(page);
  expect(reopened.cells).toEqual(executed.cells);
  expect(reopened.entries).toEqual(executed.entries);
});

test('repeated Run all keeps saved source and empty prompt counts stable', async ({ page }) => {
  const lines = ['ldc.i4 6', 'ldc.i4 7', 'mul'];
  await open(page, document(lines));
  let previous;
  for (let attempt = 0; attempt < 5; attempt++) {
    const count = await page.evaluate(() => window.ilreplSessionCount);
    await page.getByRole('button', { name: 'Run all', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
    await expect(page.locator('#session-file-message')).toContainText('Execution finished');
    await expect(page.locator('#terminal')).toContainText('= 42 : int32');
    await expect.poll(() => outputCount(page, 'il[2]>')).toBe(1);

    const saved = await download(page);
    expect(saved.entries.flatMap(entry => entry.source)).toEqual([...lines, 'ret']);
    expect(saved.cells).toHaveLength(1);
    expect(saved.cells[0].state).toBe('succeeded');
    expect(saved.editor.lines.join('\n')).toBe('');
    if (previous) {
      expect(saved.entries).toEqual(previous.entries);
      expect(saved.cells).toEqual(previous.cells);
    }
    previous = saved;
  }
});

test('sharing opens an editable experiment without replay', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  const source = experiment();
  await open(page, source);
  await page.getByRole('button', { name: 'Share', exact: true }).click();
  await expect(page.locator('#session-file-message')).toContainText('Share link copied');
  const link = await page.evaluate(() => navigator.clipboard.readText());
  expect(link).toContain('#session=v1.');
  expect(Buffer.byteLength(link, 'utf8')).toBeLessThanOrEqual(16 * 1024);
  await context.clearPermissions();
  await context.grantPermissions([], { origin: new URL(page.url()).origin });
  await page.getByRole('button', { name: 'Share', exact: true }).click();
  const selectable = page.getByRole('textbox', { name: 'Session share link' });
  await expect(selectable).toHaveValue(link);
  await expect(selectable).toBeFocused();
  const selection = await selectable.evaluate(field => ({
    readOnly: field.readOnly, selected: field.value.slice(field.selectionStart, field.selectionEnd),
  }));
  expect(selection).toEqual({ readOnly: true, selected: link });
  const shared = await context.newPage();
  await shared.goto(link);
  await ready(shared);
  await expect(shared.locator('#terminal')).toContainText(openNotice);
  await expect(shared.locator('#terminal')).toContainText('// unsent Ω source');
  await expect(shared.locator('#terminal')).toContainText(`il[1]> ldstr "${effect}"`);
  expect(await outputCount(shared, effect)).toBe(0);
  const reopened = await download(shared);
  expect(reopened.entries.map(entry => entry.source)).toEqual(source.entries.map(entry => entry.source));
  expect(reopened.cells).toEqual([]);
});

test('a link that exceeds the limit downloads a complete editable file', async ({ page }) => {
  const source = document(['// ' + randomBytes(24 * 1024).toString('base64')], ['// preserve this draft']);
  await open(page, source, 'large-example.ilrepl.json');
  const saved = await download(page, () => page.getByRole('button', { name: 'Share', exact: true }).click());
  await expect(page.locator('#session-file-message')).toContainText('too large for a link');
  expect(saved.entries.map(entry => entry.source)).toEqual(source.entries.map(entry => entry.source));
  expect(saved.editor.lines).toEqual(source.editor.lines);
  expect(saved.cells).toEqual([]);
  await open(page, saved);
  const reopened = await download(page);
  expect(reopened.entries).toEqual(saved.entries);
  expect(reopened.editor.lines).toEqual(source.editor.lines);
});

test('invalid open retains the active runtime and unsent draft', async ({ page }) => {
  await focus(page);
  await page.keyboard.type('ldc.i4.s 37 // keep this draft');
  await expect(page.locator('#terminal')).toContainText('37 // keep this draft');
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await (await chooser).setFiles({ name: 'invalid.ilrepl.json', mimeType: 'application/json', buffer: Buffer.from('{ invalid') });
  await expect(page.locator('#session-file-message')).not.toBeEmpty();
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const retained = await download(page);
  expect(retained.editor.lines).toEqual(['ldc.i4.s 37 // keep this draft']);
  expect(retained.entries).toEqual([]);

  await page.goto('/try/#session=v1.invalid');
  await page.reload();
  await ready(page);
  await expect(page.locator('#terminal')).toContainText('cannot open share link');
  await typeLine(page, 'ldc.i4.7');
  await typeLine(page, 'ret');
  await expect(page.locator('#terminal')).toContainText('= 7 : int32');
});

for (const modifier of ['Control', 'Meta']) {
  test(`${modifier}+S and ${modifier}+O prevent native browser actions and use session controls`, async ({ page }) => {
    await page.evaluate(() => {
      window.sessionShortcutEvents = [];
      document.addEventListener('keydown', event => {
        if ((event.ctrlKey || event.metaKey) && ['s', 'o'].includes(event.key.toLowerCase())) {
          window.sessionShortcutEvents.push(event);
        }
      }, true);
    });
    await focus(page);
    await page.keyboard.type('// saved with the shortcut');
    const saved = await download(page, () => page.keyboard.press(modifier + '+s'));
    expect(saved.editor.lines).toEqual(['// saved with the shortcut']);
    const count = await page.evaluate(() => window.ilreplSessionCount);
    await focus(page);
    const chooser = page.waitForEvent('filechooser');
    await page.keyboard.press(modifier + '+o');
    await (await chooser).setFiles({
      name: 'shortcut.ilrepl.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(saved)),
    });
    await ready(page, count + 1);
    expect(await page.evaluate(() => window.sessionShortcutEvents.map(event => ({
      key: event.key.toLowerCase(), prevented: event.defaultPrevented,
    })))).toEqual([
      { key: 's', prevented: true },
      { key: 'o', prevented: true },
    ]);
    const reopened = await download(page);
    expect(reopened.editor.lines).toEqual(['// saved with the shortcut']);
  });
}

test('restarting an empty session keeps the fresh terminal without an open notice', async ({ page }) => {
  const initial = await terminalText(page);
  for (let attempt = 0; attempt < 2; attempt++) {
    const count = await page.evaluate(() => window.ilreplSessionCount);
    await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
    await ready(page, count + 1);
    await expect(page.locator('#terminal')).not.toContainText(openNotice);
    await expect.poll(() => terminalText(page)).toBe(initial);
  }
  const saved = await download(page);
  expect(saved.entries).toEqual([]);
  expect(saved.cells).toEqual([]);
  expect(saved.editor.lines.join('')).toBe('');
});

test('opening an empty session file still announces the explicit open', async ({ page }) => {
  await open(page, document([]), 'empty.ilrepl.json');
  await expect(page.locator('#terminal')).toContainText(openNotice);
  const saved = await download(page);
  expect(saved.entries).toEqual([]);
  expect(saved.cells).toEqual([]);
});

test('manual restart recovers accepted source and the unsent editor', async ({ page }) => {
  await typeLine(page, 'ldc.i4.s 29');
  await expect(page.locator('#terminal')).toContainText('┊ [int32]');
  await focus(page);
  await page.keyboard.type('// survives restart');
  const before = await download(page);
  expect(before.editor.lines).toEqual(['// survives restart']);
  const count = await page.evaluate(() => window.ilreplSessionCount);
  await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
  await ready(page, count + 1);
  expect(await page.evaluate(() => window.ilreplLastRestart)).toBe('button');
  await expect(page.locator('#terminal')).not.toContainText(openNotice);
  await expect(page.locator('#terminal')).not.toContainText('= 29 : int32');
  const after = await download(page);
  expect(after.entries).toEqual(before.entries);
  expect(after.editor.lines).toEqual(['// survives restart']);
  expect(after.cells).toEqual([]);
});

for (const shortcut of [false, true]) {
  test(`${shortcut ? 'Ctrl+Q' : 'typed quit'} retires the old worker and starts exactly one empty session`, async ({ page, context }) => {
    await typeLine(page, 'ldc.i4.s 19');
    await expect(page.locator('#terminal')).toContainText('stack [int32]');
    const previous = page.workers().find(worker => worker.url().endsWith('/worker.js'));
    expect(previous).toBeDefined();
    const count = await page.evaluate(() => window.ilreplSessionCount);
    const requested = context.waitForEvent('request', request => request.url() === previous.url());
    const release = Promise.withResolvers();
    // Hold the real replacement script request so the outgoing worker's lifetime can be observed without sleeps.
    await context.route(previous.url(), async route => {
      await release.promise;
      await route.continue();
    });
    try {
      if (shortcut) {
        await focus(page);
        await page.keyboard.press('Control+q');
      } else {
        await typeLine(page, '.quit');
      }
      await requested;
      await expect.poll(() => page.workers().includes(previous)).toBe(false);
      expect(await page.evaluate(() => window.ilreplReady)).toBe(false);
      expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count);
      expect(await page.evaluate(() => window.ilreplLastRestart)).toBe('quit');
    } finally {
      release.resolve();
    }

    await ready(page, count + 1);
    expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
    const fresh = await download(page);
    expect(fresh.entries).toEqual([]);
    expect(fresh.cells).toEqual([]);
    expect(fresh.editor.lines.join('')).toBe('');
    await typeLine(page, 'ldc.i4.s 42');
    await typeLine(page, 'ret');
    await expect.poll(() => outputCount(page, '= 42 : int32')).toBe(1);
    expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
  });
}

test('a hung cell restarts with its acknowledged source and does not replay', async ({ page }) => {
  await typeLine(page, 'LOOP: br LOOP');
  await expect(page.locator('#terminal')).toContainText('1 instruction');
  const count = await page.evaluate(() => window.ilreplSessionCount);
  await focus(page);
  await page.evaluate(() => window.ilreplTerminal.paste('ret\n// remaining pasted source\nldc.i4.s 73'));
  await expect(page.locator('#terminal')).toContainText('// remaining pasted source');
  await page.keyboard.press('Enter');
  await expect.poll(() => page.evaluate(() => window.ilreplLastRestart), { timeout: 45_000 }).toBe('hung');
  await ready(page, count + 1);
  await expect(page.locator('#terminal')).not.toContainText(openNotice);
  await expect(page.locator('#terminal')).toContainText('was interrupted; no code was replayed');
  const restored = await download(page);
  expect(restored.entries.some(entry => entry.source.includes('LOOP: br LOOP'))).toBe(true);
  expect(restored.cells.filter(cell => cell.state === 'succeeded')).toEqual([]);
  expect(restored.interruptions).toHaveLength(1);
  expect(restored.interruptions[0].number).toBe(1);
  expect(restored.interruptions[0].source).toEqual(['ret']);
  expect(restored.editor.lines).toEqual(['ret', '// remaining pasted source', 'ldc.i4.s 73']);
  await expect(page.locator('#session-status')).toHaveText('Ready');
  await expect(page.locator('#terminal')).not.toContainText('=');
});

test('typed file commands download or explain the page picker', async ({ page }) => {
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const saved = await download(page, () => typeLine(page, '.session save browser'));
  expect(saved.format).toBe('ilrepl-session');
  expect(saved.entries).toEqual([]);
  await expect(page.locator('#terminal')).toContainText('downloaded browser.ilrepl.json');
  const alias = await download(page, () => typeLine(page, '.save alias.ilrepl.json'));
  expect(alias.format).toBe('ilrepl-session');
  expect(alias.cells).toEqual([]);
  await typeLine(page, '.session open another.ilrepl.json');
  await expect(page.locator('#terminal')).toContainText("use the page's Open button");
  const transcript = () => terminalText(page);
  for (const [command, operation] of [
    ['.load nuget:Humanizer.Core', 'load the dependency'],
    ['.session restore', 'restore the session'],
  ]) {
    await typeLine(page, command);
    await expect.poll(transcript).toContain('use desktop ilrepl to ' + operation);
    await expect.poll(transcript).toContain('.session save example.ilrepl.json --embed');
    await expect.poll(transcript).toContain("use the page's Open button to open that file");
  }
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count);
});


test('dirty open supports cancel, save, and discard without losing the active draft', async ({ page }) => {
  const original = experiment();
  await open(page, original);
  await focus(page);
  await page.keyboard.press('End');
  await page.keyboard.type(' changed');
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const replacement = document(['ldc.i4.5']);
  const chooseReplacement = async () => {
    const chooser = page.waitForEvent('filechooser');
    await page.getByRole('button', { name: 'Open', exact: true }).click();
    await (await chooser).setFiles({
      name: 'replacement.ilrepl.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(replacement)),
    });
    await expect(page.getByRole('dialog')).toContainText('Save changes');
  };
  await chooseReplacement();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count);
  await expect(page.locator('#terminal')).toContainText('// unsent Ω source changed');
  await chooseReplacement();
  const retained = await download(page, () => page.getByRole('button', { name: 'Save', exact: true }).click());
  expect(retained.editor.lines).toEqual(['// unsent Ω source changed']);
  expect(retained.entries.map(entry => entry.source)).toEqual(original.entries.map(entry => entry.source));
  await ready(page, count + 1);
  await focus(page);
  await page.keyboard.type('// discard this draft');
  await chooseReplacement();
  await page.getByRole('button', { name: 'Save', exact: true }).focus();
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('button', { name: 'Discard', exact: true })).toBeFocused();
  await page.keyboard.press('Enter');
  await ready(page, count + 2);
  const discarded = await download(page);
  expect(discarded.editor.lines.join('')).toBe('');
  expect(discarded.entries.map(entry => entry.source)).toEqual(replacement.entries.map(entry => entry.source));
  await typeLine(page, 'ldc.i4.3');
  await typeLine(page, '.quit');
  await expect(page.locator('#terminal')).toContainText('Save changes to replacement.ilrepl.json?');
  await page.keyboard.press('Escape');
  await expect(page.locator('#terminal')).not.toContainText('Save changes to replacement.ilrepl.json?');
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 2);
});

test('shared samples retain their original image after the bundled sample changes', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await typeLine(page, '.load /samples/Greeter.dll');
  await expect(page.locator('#terminal')).toContainText('Greeter');
  await typeLine(page, 'ldc.i4.s 19');
  await typeLine(page, 'ldc.i4.s 23');
  await typeLine(page, 'call int32 [Greeter]Greeter.Hello::Add(int32, int32)');
  await expect(page.locator('#terminal')).toContainText('3 instructions');
  await page.getByRole('button', { name: 'Share', exact: true }).click();
  await expect(page.locator('#session-file-message')).toContainText('Share link copied');
  const link = await page.evaluate(() => navigator.clipboard.readText());
  const sharedDocument = JSON.parse(gunzipSync(Buffer.from(link.split('#session=v1.')[1], 'base64url')));
  expect(sharedDocument.assets).toHaveLength(1);
  expect(sharedDocument.references[0].assets[0].hash).toMatch(/^[0-9a-f]{64}$/);
  const original = await readFile(sampleUrl);
  expect(Buffer.from(sharedDocument.assets[0].image, 'base64')).toEqual(original);
  expect(sharedDocument.assets[0].hash).toBe(sharedDocument.references[0].assets[0].hash);
  // Change a real user string without changing the PE layout, as a later sample update might do.
  const updated = Buffer.from(original);
  const greeting = updated.indexOf(Buffer.from('Hello, ', 'utf16le'));
  expect(greeting).toBeGreaterThan(0);
  updated.write('Howdy, ', greeting, 'utf16le');
  expect(createHash('sha256').update(updated).digest('hex')).not.toBe(sharedDocument.assets[0].hash);
  let updatedRequests = 0;
  await context.route('**/samples/Greeter.dll', async route => {
    updatedRequests++;
    await route.fulfill({ contentType: 'application/octet-stream', body: updated });
  });
  const shared = await context.newPage();
  await shared.goto(link);
  await ready(shared);
  expect(updatedRequests).toBeGreaterThan(0);
  await expect(shared.locator('#terminal')).not.toContainText('missing assets');
  await expect(shared.locator('#terminal')).not.toContainText('= 42 : int32');
  await typeLine(shared, '.session run');
  await expect(shared.locator('#session-file-message')).toContainText(/Running the saved source|Execution finished/);
  await expect(shared.locator('#terminal')).toContainText('= 42 : int32');
  await expect(shared.locator('#session-file-message')).toContainText('Execution finished');
  const portable = await download(shared);
  expect(portable.assets).toHaveLength(1);
  expect(portable.assets[0].hash).toBe(sharedDocument.references[0].assets[0].hash);
  await typeLine(shared, '.clear');
  await typeLine(shared, 'ldstr "reader"');
  await typeLine(shared, 'call string [Greeter]Greeter.Hello::Say(string)');
  await typeLine(shared, 'ret');
  await expect(shared.locator('#terminal')).toContainText('Hello, reader!');
  await expect(shared.locator('#terminal')).not.toContainText('Howdy, reader!');
});

test('unsupported documents and mismatched assets preserve the current draft; oversized links are bounded', async ({ page }) => {
  await focus(page);
  await page.keyboard.type('// keep this source');
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const unsupported = document([]);
  unsupported.version = 999;
  const mismatched = await embeddedExperiment();
  mismatched.assets[0].hash = '0'.repeat(64);
  for (const [source, diagnostic] of [[unsupported, 'unsupported session format'], [mismatched, 'mismatched content hash']]) {
    const chooser = page.waitForEvent('filechooser');
    await page.getByRole('button', { name: 'Open', exact: true }).click();
    await (await chooser).setFiles({
      name: 'rejected.ilrepl.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(source)),
    });
    await expect(page.locator('#session-file-message')).toContainText(diagnostic);
    expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  }
  const preserved = await download(page);
  expect(preserved.editor.lines).toEqual(['// keep this source']);
  const oversized = document(['// ' + 'x'.repeat(1024 * 1024)]);
  const fragment = '#session=v1.' + gzipSync(JSON.stringify(oversized)).toString('base64url');
  expect(fragment.length).toBeLessThan(16 * 1024);
  await page.goto('/try/' + fragment);
  await page.reload();
  await ready(page);
  await expect(page.locator('#terminal')).toContainText('1 MiB expanded-document limit');
  await typeLine(page, 'ldc.i4.3');
  await typeLine(page, 'ret');
  await expect(page.locator('#terminal')).toContainText('= 3 : int32');
});


for (const origin of ['package', 'project']) {
  test(`missing ${origin} assets remain editable and direct browser execution to desktop recovery`, async ({ page }) => {
    const source = experiment();
    source.entries = source.entries.map(entry => ({ ...entry, kind: 'Source' }));
    const identity = randomUUID();
    const request = origin === 'package' ? 'Unavailable.Package' : '../Unavailable/Unavailable.csproj';
    source.references = [{
      identity, origin, request, dependencies: [], frameworks: [], framework: 'net10.0',
      assets: [{
        name: 'Unavailable.Dependency', hash: '1'.repeat(64), kind: 'managed',
        ...(origin === 'package' ? { packagePath: 'lib/net10.0/Unavailable.dll' } : { path: 'bin/Unavailable.dll' }),
      }],
      ...(origin === 'package' ? { requestedVersion: '[1.2.3]', version: '1.2.3' } : { configuration: 'Debug' }),
    }];
    source.entries.unshift({
      identity: randomUUID(), number: 1, kind: 'Reference', reference: identity,
      source: [origin === 'package' ? '.load nuget:Unavailable.Package,1.2.3' : '.load ../Unavailable/Unavailable.csproj'],
    });
    await open(page, source, `${origin}.ilrepl.json`);
    await expect.poll(() => terminalText(page)).toContain('desktop ilrepl');
    await expect.poll(() => terminalText(page)).toContain('--embed');
    expect(await outputCount(page, effect)).toBe(0);
    await focus(page);
    await page.keyboard.press('End');
    await page.keyboard.type(' remains editable');
    const saved = await download(page);
    expect(saved.references).toEqual(source.references);
    expect(saved.assets).toEqual([]);
    expect(saved.editor.lines).toEqual(['// unsent Ω source remains editable']);
    expect(saved.entries).toEqual(source.entries);
    const count = await page.evaluate(() => window.ilreplSessionCount);
    await page.getByRole('button', { name: 'Run all', exact: true }).click();
    await ready(page, count + 1);
    await expect(page.locator('#session-file-message')).toContainText('desktop ilrepl');
    await expect(page.locator('#session-file-message')).toContainText('--embed');
    expect(await outputCount(page, effect)).toBe(0);
    const retained = await download(page);
    expect(retained.references).toEqual(saved.references);
    expect(retained.entries).toEqual(saved.entries);
    expect(retained.cells).toEqual([]);
    expect(retained.editor.lines).toEqual(saved.editor.lines);
  });
}

test('explicit browser embed acknowledges inclusion and preserves the same dependency images', async ({ page }) => {
  await open(page, await embeddedExperiment());
  const implicit = await download(page);
  await focus(page);
  await page.keyboard.press('Control+c');
  const explicit = await download(page, () => typeLine(page, '.session save embedded.ilrepl.json --embed'));
  await expect.poll(() => terminalText(page)).toContain('--embed is already applied');
  expect(explicit.assets).toHaveLength(1);
  expect(explicit.assets).toEqual(implicit.assets);
  expect(explicit.references).toEqual(implicit.references);
  expect(explicit.entries).toEqual(implicit.entries);
  expect(explicit.cells).toEqual([]);
  expect(await outputCount(page, effect)).toBe(0);
});

test('browser import accepts its 8 MiB boundary and rejects the next byte without replacing source', async ({ page }) => {
  const source = document([], ['// exact boundary draft']);
  const encoded = Buffer.from(JSON.stringify(source));
  const boundary = Buffer.alloc(8 * 1024 * 1024, ' ');
  encoded.copy(boundary);
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await (await chooser).setFiles({ name: 'boundary.ilrepl.json', mimeType: 'application/json', buffer: boundary });
  await ready(page, count + 1);
  await expect(page.locator('#terminal')).toContainText('// exact boundary draft');
  const rejected = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await (await rejected).setFiles({
    name: 'oversized.ilrepl.json', mimeType: 'application/json', buffer: Buffer.concat([boundary, Buffer.from(' ')]),
  });
  await expect(page.locator('#session-file-message')).toContainText('Browser session files are limited to 8 MiB');
  await expect(page.locator('#session-file-message')).toContainText('desktop ilrepl');
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const runtime = page.workers().find(worker => worker.url().endsWith('/worker.js'));
  expect(runtime).toBeDefined();
  const diagnostic = await runtime.evaluate(async value => {
    try {
      await self.__ilreplWorkspace('validate', value);
      return null;
    } catch (error) {
      return String(error);
    }
  }, boundary.toString('utf8') + ' ');
  expect(diagnostic).toContain('browser session files are limited to 8 MiB');
  expect(diagnostic).toContain('desktop ilrepl');
  const retained = await download(page);
  expect(retained.editor.lines).toEqual(source.editor.lines);
  expect(retained.entries).toEqual([]);
  expect(retained.assets).toEqual([]);
});

test('restart retires execution before boot and replays replacement input without submitting it', async ({ page, context }) => {
  await focus(page);
  await page.keyboard.type('// retained Ω');
  const previous = page.workers().find(worker => worker.url().endsWith('/worker.js'));
  expect(previous).toBeDefined();
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const requested = context.waitForEvent('request', request => request.url() === previous.url());
  const release = Promise.withResolvers();
  await context.route(previous.url(), async route => {
    await release.promise;
    await route.continue();
  });
  try {
    await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
    await requested;
    await expect.poll(() => page.workers().includes(previous)).toBe(false);
    expect(await page.evaluate(() => window.ilreplReady)).toBe(false);
    await focus(page);
    await page.keyboard.press('Enter');
    await page.keyboard.type('ldc.i4.s 42');
    await page.keyboard.press('Enter');
    await page.keyboard.type('ret');
    await page.keyboard.press('Enter');
    await page.keyboard.type('// replacement Δ');
    // A second request shares the in-progress replacement rather than creating another runtime.
    await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
  } finally {
    release.resolve();
  }
  await ready(page, count + 1);
  const restored = await download(page);
  expect(restored.editor.lines).toEqual(['// retained Ω', 'ldc.i4.s 42', 'ret', '// replacement Δ']);
  expect(restored.entries).toEqual([]);
  expect(restored.cells).toEqual([]);
  expect(await outputCount(page, '= 42 : int32')).toBe(0);
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
});

for (const action of ['Download', 'Run all']) {
  test(`retiring a worker settles pending ${action} without changing the replacement`, async ({ page }) => {
    await typeLine(page, 'LOOP: br LOOP');
    await expect(page.locator('#terminal')).toContainText('1 instruction');
    await typeLine(page, 'ret');
    await expect.poll(() => page.evaluate(() => window.ilreplWorkspaceState.pendingSubmission)).toBe(1);
    const previous = page.workers().find(worker => worker.url().endsWith('/worker.js'));
    const count = await page.evaluate(() => window.ilreplSessionCount);
    const downloads = [];
    page.on('download', value => downloads.push(value));
    await page.getByRole('button', { name: action, exact: true }).click();
    await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
    await ready(page, count + 1);
    await expect.poll(() => page.workers().includes(previous)).toBe(false);
    await expect(page.locator('#session-file-message')).not.toContainText('downloaded');
    expect(downloads).toEqual([]);
    const restored = await download(page);
    expect(restored.interruptions).toHaveLength(1);
    expect(restored.cells.find(cell => cell.number === 1)?.state).toBe('interrupted');
    expect(restored.entries.flatMap(entry => entry.source)).toContain('LOOP: br LOOP');
  });
}

test('assembly save downloads a managed PE instead of leaving the image in the worker filesystem', async ({ page }) => {
  await typeLine(page, '.method int32 Answer() {');
  await typeLine(page, 'ldc.i4.s 42');
  await typeLine(page, 'ret');
  await typeLine(page, '}');
  const downloading = page.waitForEvent('download');
  await typeLine(page, '.save browser-answer.dll');
  const result = await downloading;
  expect(result.suggestedFilename()).toBe('browser-answer.dll');
  const bytes = await readFile(await result.path());
  expect(bytes.subarray(0, 2).toString('ascii')).toBe('MZ');
  const pe = bytes.readUInt32LE(0x3c);
  expect(bytes.subarray(pe, pe + 4)).toEqual(Buffer.from([0x50, 0x45, 0, 0]));
  expect(bytes.includes(Buffer.from('Answer\0'))).toBe(true);
  expect(bytes.includes(Buffer.from('browser-answer\0'))).toBe(true);
  const runtime = page.workers().find(worker => worker.url().endsWith('/worker.js'));
  expect(await runtime.evaluate(image => self.__ilreplConformance(image, 'IlRepl.Cell', 'Answer'),
    bytes.toString('base64'))).toBe(42);
  await expect(page.locator('#terminal')).toContainText('downloaded browser-answer.dll');
  await expect(page.locator('#session-file-message')).toHaveText('Assembly downloaded.');
  await typeLine(page, 'call int32 Answer()');
  await typeLine(page, 'ret');
  await expect.poll(() => outputCount(page, '= 42 : int32')).toBe(1);
});

test('replacement rejects input and acknowledgements belonging to a retired generation', async ({ page }) => {
  const oldGeneration = await page.evaluate(() => window.ilreplGeneration);
  const count = await page.evaluate(() => window.ilreplSessionCount);
  await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
  await ready(page, count + 1);
  await focus(page);
  await page.keyboard.type('// current Ω');
  const runtime = page.workers().find(worker => worker.url().endsWith('/worker.js'));
  expect(runtime).toBeDefined();
  await runtime.evaluate(generation => {
    self.onmessage({ data: { type: 'input', generation, sequence: 1, data: btoa('.quit\r') } });
    self.onmessage({ data: { type: 'workspace-ack', generation, identity: 1 } });
    self.onmessage({ data: { type: 'input', sequence: 1, data: btoa('.quit\r') } });
  }, oldGeneration);
  const retained = await download(page);
  expect(retained.editor.lines).toEqual(['// current Ω']);
  expect(retained.entries).toEqual([]);
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
  expect(await page.evaluate(() => window.ilreplWorkspaceState.pendingInput)).toBe(0);
});

test('manual restart supersedes scheduled watchdog recovery without starting a second replacement', async ({ page, context }) => {
  await typeLine(page, 'LOOP: br LOOP');
  await typeLine(page, 'ret');
  await expect.poll(() => page.evaluate(() => window.ilreplWorkspaceState.pendingSubmission)).toBe(1);
  const previous = page.workers().find(worker => worker.url().endsWith('/worker.js'));
  const count = await page.evaluate(() => window.ilreplSessionCount);
  const requested = context.waitForEvent('request', request => request.url() === previous.url());
  const release = Promise.withResolvers();
  await context.route(previous.url(), async route => {
    await release.promise;
    await route.continue();
  });
  try {
    await expect.poll(() => page.evaluate(() => window.ilreplLastRestart), { timeout: 45_000 }).toBe('hung');
    await page.getByRole('button', { name: 'Restart the session', exact: true }).click();
    await requested;
    expect(await page.evaluate(() => window.ilreplWorkspaceState.restartScheduled)).toBe(false);
    await expect.poll(() => page.workers().includes(previous)).toBe(false);
    await focus(page);
    await page.keyboard.type('// watchdog replacement');
  } finally {
    release.resolve();
  }
  await ready(page, count + 1);
  const restored = await download(page);
  expect(restored.interruptions).toHaveLength(1);
  expect(restored.editor.lines.join('\n')).toContain('// watchdog replacement');
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
});

test('typed session restart delegates replacement to the page and preserves accepted definitions', async ({ page }) => {
  await typeLine(page, '.method int32 Answer() {');
  await typeLine(page, 'ldc.i4.s 42');
  await typeLine(page, 'ret');
  await typeLine(page, '}');
  const previous = page.workers().find(worker => worker.url().endsWith('/worker.js'));
  const count = await page.evaluate(() => window.ilreplSessionCount);
  await typeLine(page, '.session restart');
  await ready(page, count + 1);
  await expect.poll(() => page.workers().includes(previous)).toBe(false);
  await expect(page.locator('#terminal')).not.toContainText(openNotice);
  await typeLine(page, 'call int32 Answer()');
  await typeLine(page, 'ret');
  await expect.poll(() => outputCount(page, '= 42 : int32')).toBe(1);
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
});

test('a download acknowledgement marks only its captured document as saved', async ({ page }) => {
  const runtime = page.workers().find(worker => worker.url().endsWith('/worker.js'));
  await focus(page);
  await page.keyboard.type('// first document');
  const first = await runtime.evaluate(() => self.__ilreplWorkspace('capture', ''));
  await page.keyboard.type(' changed');
  const second = await runtime.evaluate(() => self.__ilreplWorkspace('capture', ''));
  expect(JSON.parse(first).editor.lines).toEqual(['// first document']);
  expect(JSON.parse(second).editor.lines).toEqual(['// first document changed']);
  await runtime.evaluate(document => self.__ilreplWorkspace('saved', JSON.stringify({
    path: 'first.ilrepl.json', document,
  })), first);
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await (await chooser).setFiles({
    name: 'replacement.ilrepl.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(document([]))),
  });
  await expect(page.getByRole('dialog')).toContainText('Save changes');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  const retained = await download(page);
  expect(retained.editor.lines).toEqual(['// first document changed']);
});

test('a file read completing after Restart cannot replace the newer runtime', async ({ page }) => {
  await focus(page);
  await page.keyboard.type('// keep this draft');
  const count = await page.evaluate(() => window.ilreplSessionCount);
  await page.locator('#session-files input[type=file]').evaluate((picker, source) => {
    const transfer = new DataTransfer();
    transfer.items.add(new File([source], 'stale.ilrepl.json', { type: 'application/json' }));
    picker.files = transfer.files;
    // Real File.text() completes asynchronously; Restart retires its generation in this same browser task.
    picker.dispatchEvent(new Event('change', { bubbles: true }));
    document.getElementById('session-restart').click();
  }, JSON.stringify(document(['ldc.i4.s 99'])));
  await ready(page, count + 1);
  const retained = await download(page);
  expect(retained.entries).toEqual([]);
  expect(retained.editor.lines).toEqual(['// keep this draft']);
  expect(await page.evaluate(() => window.ilreplSessionCount)).toBe(count + 1);
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
