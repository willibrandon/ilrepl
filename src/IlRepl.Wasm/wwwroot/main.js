// main.js runs on the page. It owns xterm.js and supervises the Web Worker that hosts .NET.
// The script's own URL decides where the worker lives, so the same file works from any base path.
// The terminal is exposed as window.ilreplTerminal so tests can read its size and buffer;
// window.ilreplReady and window.ilreplSessionCount tell them when a session is up.

(async () => {
  const scriptUrl = document.currentScript ? document.currentScript.src : new URL('main.js', location.href).href;
  const baseUrl = scriptUrl.substring(0, scriptUrl.lastIndexOf('/') + 1);
  const { createComparisonSupervisor } = await import(baseUrl + 'comparison-supervisor.js');
  const comparisons = createComparisonSupervisor(baseUrl);
  const { createWorkspaceControls } = await import(baseUrl + 'workspace-controls.js');
  const container = document.getElementById('terminal');
  if (!container) return;
  const statusEl = document.getElementById('session-status');
  const restartButton = document.getElementById('session-restart');
  const setStatus = (text) => { if (statusEl) statusEl.textContent = text; };
  const rows = 24;
  const fontFamily = '"JetBrains Mono", "Cascadia Code", "Fira Code", Menlo, Monaco, monospace';
  window.ilreplReady = false;
  window.ilreplSessionCount = 0;
  window.ilreplLastRestart = null;
  window.ilreplLastCopy = null;

  try {
    if (!document.querySelector('link[href*="xterm"]')) {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = 'https://esm.sh/@xterm/xterm@5.5.0/css/xterm.css';
      document.head.appendChild(link);
      await new Promise((resolve) => { link.onload = resolve; link.onerror = resolve; });
    }

    // Cell metrics are measured when the terminal opens, so the font must be settled first.
    try { await document.fonts.load('14px "JetBrains Mono"'); } catch { }
    await document.fonts.ready;

    const { Terminal } = await import('https://esm.sh/@xterm/xterm@5.5.0');
    const { FitAddon } = await import('https://esm.sh/@xterm/addon-fit@0.10.0');

    const term = new Terminal({
      cols: 80,
      rows,
      cursorBlink: true,
      cursorStyle: 'block',
      fontSize: 14,
      fontFamily,
      theme: {
        background: '#121218',
        foreground: '#e0e0e0',
        cursor: '#4fd1c5',
        selectionBackground: '#00644080',
        black: '#121218',
        brightBlack: '#3c3c50',
        brightWhite: '#ffffff',
      },
      allowProposedApi: true,
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(container);
    window.ilreplTerminal = term;

    // Only the column count follows the box; the row count stays fixed and the box grows to it.
    const fitColumns = () => {
      const proposed = fit.proposeDimensions();
      const cols = proposed && proposed.cols > 20 ? proposed.cols : 80;
      if (cols !== term.cols) term.resize(cols, rows);
    };
    fitColumns();

    // The supervisor owns worker replacement after quitting, restarting, or a runtime failure.
    // A session that fails before it has run for a few seconds is left failed, since starting it
    // again would most likely fail the same way.
    const heartbeatLimit = 8000;
    const restartDelay = 1500;
    let worker = null;
    let preparing = null;
    let preparationTimer = null;
    const queuedInput = [];
    let checkpoint = null;
    let preferences = 'true,false';
    let nextGeneration = 0;
    const workspace = createWorkspaceControls({
      getWorker: () => worker,
      getCheckpoint: () => checkpoint,
      replace: (source, path, run) => startWorker(source, path, run),
      setStatus,
      focus: () => term.focus(),
    });
    let generation = 0;
    let lastMessageAt = 0;
    let readyAt = 0;
    let restartTimer = null;
    let documentationUrl = null;
    let helpSequence = 0;
    let helpActive = false;
    let helpPending = false;
    const helpQueue = [];
    const resetHelp = () => {
      documentationUrl = null;
      helpSequence = 0;
      helpActive = false;
      helpPending = false;
      helpQueue.length = 0;
    };

    const sendResize = () => { if (worker) worker.postMessage({ type: 'resize', cols: term.cols, rows: term.rows }); };

    const stopWorker = () => {
      if (restartTimer) { clearTimeout(restartTimer); restartTimer = null; }
      comparisons.stop();
      resetHelp();
      if (worker) {
        workspace.stop(worker);
        worker.onmessage = null;
        worker.onerror = null;
        worker.terminate();
        worker = null;
      }
      window.ilreplReady = false;
      readyAt = 0;
    };

    window.addEventListener('pagehide', (event) => {
      if (!event.persisted) stopWorker();
    });

    const scheduleRestart = (delay) => {
      if (restartTimer) clearTimeout(restartTimer);
      restartTimer = setTimeout(() => { restartTimer = null; startWorker(); }, delay);
    };

    const recordInterruption = () => {
      if (!checkpoint?.pendingSubmission) return;
      const document = checkpoint.document;
      document.interruptions ||= [];
      const identity = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
      document.interruptions.push({ identity, number: checkpoint.pendingSubmission,
        source: checkpoint.pendingSource || [] });
      checkpoint = { ...checkpoint, document, dirty: true, pendingSubmission: 0 };
    };

    const fail = (reason, detail) => {
      const hadRun = readyAt !== 0 && Date.now() - readyAt > 3000;
      recordInterruption();
      stopWorker();
      window.ilreplLastRestart = reason;
      if (hadRun) {
        setStatus(reason === 'hung' ? 'Not responding, restarting' : 'Restarting');
        scheduleRestart(restartDelay);
      } else {
        setStatus('Failed');
        term.write('\r\n' + detail + '\r\n');
      }
    };

    const startWorker = (source = checkpoint ? JSON.stringify(checkpoint.document) : '', path = checkpoint?.path || null, run = null) => {
      if (preparing) preparing.terminate();
      if (preparationTimer) clearTimeout(preparationTimer);
      const candidate = new Worker(baseUrl + 'worker.js', { type: 'module' });
      preparing = candidate;
      const own = ++nextGeneration;
      let candidateCheckpoint = null;
      let retainedDocument = null;
      const assets = new Map();
      const retainCheckpoint = msg => {
        const document = JSON.parse(msg.document);
        for (const asset of document.assets) assets.set(asset.hash, asset);
        document.assets = msg.assetHashes.map(hash => assets.get(hash));
        document.entries = [...(retainedDocument?.entries || []).slice(0, msg.entryPrefix), ...document.entries];
        const cells = new Map((retainedDocument?.cells || []).map(cell => [cell.number, cell]));
        for (const cell of document.cells) cells.set(cell.number, cell);
        document.cells = msg.cellNumbers.map(number => cells.get(number));
        retainedDocument = document;
        return { document, path: msg.path, dirty: msg.dirty, preferences: msg.preferences,
          pendingSubmission: msg.pendingSubmission, pendingSource: msg.pendingSource };
      };
      const output = [];
      const flushQueuedInput = () => {
        for (const input of queuedInput.splice(0)) send(input.text, input.binary);
      };
      const rejectCandidate = detail => {
        if (preparing !== candidate) return;
        clearTimeout(preparationTimer);
        preparationTimer = null;
        preparing = null;
        candidate.terminate();
        workspace.error(detail);
        setStatus(worker ? 'Ready' : 'Failed');
        flushQueuedInput();
      };
      preparationTimer = setTimeout(() => rejectCandidate(
        'Opening the session timed out; try again or use Restart.'), 45_000);
      lastMessageAt = Date.now();
      setStatus('Loading');
      candidate.postMessage({ type: 'workspace-init', document: source, path, preferences });
      candidate.postMessage({ type: 'resize', cols: term.cols, rows: term.rows });

      candidate.onmessage = (e) => {
        const incoming = e.data;
        if (preparing === candidate) {
          if (incoming.type === 'workspace-checkpoint') {
            candidateCheckpoint = retainCheckpoint(incoming);
            candidate.postMessage({ type: 'workspace-ack', identity: incoming.identity });
            return;
          }
          if (incoming.type === 'output') { output.push(incoming.data); return; }
          if (incoming.type === 'error') {
            rejectCandidate(incoming.message);
            return;
          }
          if (incoming.type !== 'ready') return;
          clearTimeout(preparationTimer);
          preparationTimer = null;
          stopWorker();
          preparing = null;
          worker = candidate;
          generation = own;
          checkpoint = candidateCheckpoint;
          preferences = checkpoint?.preferences || preferences;
          term.reset();
          for (const data of output) term.write(new Uint8Array(data));
        }
        if (own !== generation) return;
        lastMessageAt = Date.now();
        const msg = e.data;
        if (msg.type === 'workspace-checkpoint') {
          checkpoint = retainCheckpoint(msg);
          preferences = checkpoint.preferences;
          worker.postMessage({ type: 'workspace-ack', identity: msg.identity });
        } else if (msg.type === 'workspace-editor') {
          if (checkpoint) {
            const document = checkpoint.document;
            const editor = JSON.parse(msg.editor);
            const changed = document.editor.lines.join('\n') !== editor.lines.join('\n');
            document.editor = editor;
            checkpoint = { ...checkpoint, document, dirty: checkpoint.dirty || changed };
          }
        } else if (msg.type.startsWith('workspace-')) {
          workspace.message(msg, worker);
        } else if (msg.type === 'output') {
          term.write(new Uint8Array(msg.data));
        } else if (msg.type === 'documentation-target') {
          if (msg.sequence === helpSequence) {
            helpActive = msg.active === true;
            helpPending = false;
            documentationUrl = typeof msg.url === 'string'
              && /^https:\/\/ilrepl\.dev\/reference\/opcodes\/#[-a-z0-9]+$/.test(msg.url) ? msg.url : null;
            while (!helpPending && helpQueue.length) {
              const next = helpQueue.shift();
              send(next.text, next.binary);
            }
          }
        } else if (msg.type === 'comparison-run') {
          comparisons.run(msg, worker);
        } else if (msg.type === 'comparison-cancel') {
          comparisons.cancel(msg.identity);
        } else if (msg.type === 'progress') {
          setStatus(`Loading runtime ${msg.loaded}/${msg.total}`);
        } else if (msg.type === 'workerReady') {
          setStatus('Starting');
          sendResize();
        } else if (msg.type === 'ready') {
          readyAt = Date.now();
          setStatus('Ready');
          sendResize();
          term.focus();
          window.ilreplSessionCount++;
          window.ilreplReady = true;
          workspace.ready();
          flushQueuedInput();
          if (run !== null) {
            const selected = run;
            run = null;
            workspace.running();
            workspace.request('run', selected).then(workspace.completed, workspace.error);
          }
        } else if (msg.type === 'exited') {
          // Quit starts a fresh worker, dropping all previous runtime state and source.
          stopWorker();
          window.ilreplLastRestart = 'quit';
          setStatus('Restarting');
          checkpoint = null;
          startWorker('', null);
        } else if (msg.type === 'error') {
          console.error('ilrepl worker error', msg.message, msg.stack);
          fail('error', msg.message);
        }
      };
      candidate.onerror = (e) => {
        if (preparing === candidate) {
          rejectCandidate(e.message);
          return;
        }
        if (own !== generation) return;
        console.error('ilrepl worker error', e.message, e.filename, e.lineno);
        fail('error', 'The worker failed: ' + e.message);
      };
    };

    // Heartbeats stop when the worker's event loop is blocked. Hidden tabs are skipped because
    // browsers slow their timers down, which would look the same.
    setInterval(() => {
      if (!worker || document.visibilityState !== 'visible') return;
      if (Date.now() - lastMessageAt > heartbeatLimit) fail('hung', 'The session stopped responding.');
    }, 1000);
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') lastMessageAt = Date.now();
    });

    if (restartButton) {
      restartButton.addEventListener('click', () => {
        window.ilreplLastRestart = 'button';
        setStatus('Restarting');
        recordInterruption();
        startWorker();
      });
    }

    const send = (text, binary = false) => {
      if (preparing) {
        queuedInput.push({ text, binary });
        return;
      }
      if (!worker) return;
      if (helpPending) {
        helpQueue.push({ text, binary });
        return;
      }
      // Wait for an actual UI action, not a raw-byte read, before enabling a new link.
      const helpKey = text === '\x1bOP' || text === '\x1b[11~'
        || (helpActive && ['\t', '\x1b[Z', '\r', '\x1b', '\x1b[19~', '\x1b[19;2~'].includes(text));
      if (helpKey) {
        helpSequence++;
        helpPending = true;
      }
      documentationUrl = null;
      const data = binary ? text : String.fromCharCode(...new TextEncoder().encode(text));
      worker.postMessage({ type: 'input', data: btoa(data) });
    };
    term.onData((text) => send(text));
    term.onBinary((data) => send(data, true));
    term.onResize(() => sendResize());
    container.addEventListener('keydown', (event) => {
      const tab = event.key === 'Tab' && !event.ctrlKey && !event.altKey && !event.metaKey;
      const interrupt = event.key.toLowerCase() === 'c' && event.ctrlKey && !event.shiftKey && !event.altKey
        && !event.metaKey && !term.hasSelection();
      if (tab || interrupt) {
        event.preventDefault();
        event.stopImmediatePropagation();
        send(tab ? (event.shiftKey ? '\x1b[Z' : '\t') : '\x03');
      }
    }, true);
    let resizeTimer = null;
    window.addEventListener('resize', () => {
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(fitColumns, 100);
    });

    // Selection and copy are the terminal's own: the app never takes the mouse, so a drag
    // selects here, and Ctrl+C, Cmd+C, or y copies the selection instead of sending the key,
    // y being the desktop's yank.
    // Copies text with the copy command inside the key's own gesture, from a scratch textarea
    // holding it, since the command copies a selection in the page and the terminal's is not one.
    const copyNow = (text) => {
      const scratch = document.createElement('textarea');
      scratch.value = text;
      scratch.setAttribute('readonly', '');
      scratch.style.position = 'fixed';
      scratch.style.top = '0';
      scratch.style.opacity = '0';
      document.body.appendChild(scratch);
      scratch.focus();
      scratch.select();
      let copied = false;
      try { copied = document.execCommand('copy'); } catch { copied = false; }
      document.body.removeChild(scratch);
      term.focus();
      return copied;
    };

    // A key that copied is swallowed whole: its keypress and the text it would put in the
    // helper textarea as well, or the y would reach the prompt too.
    let swallowed = null;
    term.attachCustomKeyEventHandler((e) => {
      const key = e.key.toLowerCase();
      if (swallowed === key) {
        if (e.type === 'keyup') swallowed = null;
        e.preventDefault();
        return false;
      }
      if (e.type === 'keydown' && helpPending && key === 'enter'
        && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
        // Preserve queued source/editor actions, but never defer a browser popup beyond its gesture.
        send('\r');
        swallowed = key;
        e.preventDefault();
        return false;
      }
      if (e.type === 'keydown' && documentationUrl && key === 'enter'
        && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
        // Opening here retains the browser's user gesture; a worker round trip would lose it.
        window.open(documentationUrl, '_blank', 'noopener,noreferrer');
        swallowed = key;
        e.preventDefault();
        return false;
      }
      if (e.type === 'keydown' && key === 'f1') e.preventDefault();
      if (e.type !== 'keydown' || e.altKey || !term.hasSelection()) return true;
      const copies = ((e.ctrlKey || e.metaKey) && key === 'c') || (!e.ctrlKey && !e.metaKey && key === 'y');
      if (!copies) return true;
      // The copy command needs no permission and works on plain http; the clipboard API is the
      // second try. The selection is cleared only once a copy has landed, so a copy that fails
      // leaves it there to try again.
      const text = term.getSelection();
      if (copyNow(text)) {
        window.ilreplLastCopy = text;
        term.clearSelection();
      } else if (navigator.clipboard) {
        navigator.clipboard.writeText(text).then(() => {
          window.ilreplLastCopy = text;
          term.clearSelection();
        }).catch(() => {});
      }
      swallowed = key;
      e.preventDefault();
      return false;
    });

    // The terminal cell under a point on the page, counted from one, or null before the
    // terminal has opened.
    const cellAt = (clientX, clientY) => {
      const screen = term.element && term.element.querySelector('.xterm-screen');
      if (!screen) return null;
      const rect = screen.getBoundingClientRect();
      const cellWidth = rect.width / term.cols;
      const cellHeight = rect.height / term.rows;
      return {
        col: Math.min(term.cols, Math.max(1, Math.floor((clientX - rect.left) / cellWidth) + 1)),
        row: Math.min(term.rows, Math.max(1, Math.floor((clientY - rect.top) / cellHeight) + 1)),
        cellHeight,
      };
    };

    // Wheel notches still scroll the transcript: with no mouse mode on, xterm would turn them
    // into arrow keys, so they go to the app as the scroll reports a terminal sends, one per
    // line, from the cell under the pointer.
    term.attachCustomWheelEventHandler((e) => {
      if (!worker) return true;
      const cell = cellAt(e.clientX, e.clientY);
      if (!cell) return true;
      const lines = e.deltaMode === 1 ? e.deltaY : e.deltaMode === 2 ? e.deltaY * term.rows : e.deltaY / cell.cellHeight;
      const count = Math.min(50, Math.max(1, Math.round(Math.abs(lines))));
      const button = e.deltaY < 0 ? 64 : 65;
      send(`\x1b[<${button};${cell.col};${cell.row}M`.repeat(count));
      e.preventDefault();
      return false;
    });

    // A click anywhere in the box focuses the terminal. In the padding around the rows the
    // default action of the mousedown would move focus to the body right after, so it is
    // stopped there; inside the rows xterm handles the press itself, and selects on a drag.
    // A plain left click that did not move still reaches the app, as the press and release
    // reports a terminal sends, so a row of the palette can be taken with the mouse and a
    // click in the buffer places the caret.
    let press = null;
    container.addEventListener('mousedown', (e) => {
      if (!e.target.closest('.xterm')) e.preventDefault();
      term.focus();
      press = e.button === 0 && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey ? { x: e.clientX, y: e.clientY } : null;
    });
    container.addEventListener('mouseup', (e) => {
      term.focus();
      const start = press;
      press = null;
      if (!worker || !start || e.button !== 0) return;
      if (Math.abs(e.clientX - start.x) > 3 || Math.abs(e.clientY - start.y) > 3) return;
      const cell = cellAt(e.clientX, e.clientY);
      if (!cell) return;
      send(`\x1b[<0;${cell.col};${cell.row}M\x1b[<0;${cell.col};${cell.row}m`);
    });

    startWorker(location.hash.startsWith('#session=') ? location.hash : '', null);
  } catch (err) {
    setStatus('Failed');
    console.error('ilrepl page error', err);
    container.textContent = 'The live session could not start: ' + (err && err.message ? err.message : err);
  }
})();
