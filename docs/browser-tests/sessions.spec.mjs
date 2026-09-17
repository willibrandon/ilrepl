import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { gzipSync, gunzipSync } from 'node:zlib';
import { expect, test } from '@playwright/test';

const effect = 'SESSION_SMOKE_EXECUTED';

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
  const image = await readFile(new URL('../public/try/samples/Greeter.dll', import.meta.url));
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
    const previous = page.workers().find(worker => worker.url().endsWith('/try/worker.js'));
    expect(previous).toBeDefined();
    const count = await page.evaluate(() => window.ilreplSessionCount);
    const requested = context.waitForEvent('request', request => request.url().endsWith('/try/worker.js'));
    const release = Promise.withResolvers();
    // Hold the real replacement script request so the outgoing worker's lifetime can be observed without sleeps.
    await context.route('**/try/worker.js', async route => {
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
  const original = await readFile(new URL('../public/try/samples/Greeter.dll', import.meta.url));
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
