// main.js runs on the page. It owns xterm.js and supervises the Web Worker that hosts .NET.
// The script's own URL decides where the worker lives, so the same file works from any base path.
// The terminal is exposed as window.ilreplTerminal so tests can read its size and buffer;
// window.ilreplReady and window.ilreplSessionCount tell them when a session is up.

(async () => {
  const scriptUrl = document.currentScript ? document.currentScript.src : new URL('main.js', location.href).href;
  const baseUrl = scriptUrl.substring(0, scriptUrl.lastIndexOf('/') + 1);
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

    // The supervisor owns the worker. A quit inside the app starts a new session in the same
    // runtime. A crash, or a session that stops responding, gets a new worker. The restart button
    // does the same by hand. A session that fails before it has run for a few seconds is left
    // failed, since starting it again would most likely fail the same way.
    const heartbeatLimit = 8000;
    const restartDelay = 1500;
    let worker = null;
    let generation = 0;
    let lastMessageAt = 0;
    let readyAt = 0;
    let restartTimer = null;

    const sendResize = () => { if (worker) worker.postMessage({ type: 'resize', cols: term.cols, rows: term.rows }); };

    const stopWorker = () => {
      if (restartTimer) { clearTimeout(restartTimer); restartTimer = null; }
      if (worker) {
        worker.onmessage = null;
        worker.onerror = null;
        worker.terminate();
        worker = null;
      }
      window.ilreplReady = false;
      readyAt = 0;
    };

    const scheduleRestart = (delay) => {
      if (restartTimer) clearTimeout(restartTimer);
      restartTimer = setTimeout(() => { restartTimer = null; startWorker(); }, delay);
    };

    const fail = (reason, detail) => {
      const hadRun = readyAt !== 0 && Date.now() - readyAt > 3000;
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

    const startWorker = () => {
      stopWorker();
      const own = ++generation;
      term.reset();
      lastMessageAt = Date.now();
      setStatus('Loading');
      worker = new Worker(baseUrl + 'worker.js', { type: 'module' });
      // The worker queues this until the runtime is up, so the app's first layout matches the terminal.
      sendResize();

      worker.onmessage = (e) => {
        if (own !== generation) return;
        lastMessageAt = Date.now();
        const msg = e.data;
        if (msg.type === 'output') {
          term.write(new Uint8Array(msg.data));
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
        } else if (msg.type === 'exited') {
          // Ctrl+Q or .quit. The worker starts the next session itself; only the screen is cleared.
          window.ilreplReady = false;
          window.ilreplLastRestart = 'quit';
          readyAt = 0;
          setStatus('Restarting');
          term.reset();
        } else if (msg.type === 'error') {
          console.error('ilrepl worker error', msg.message, msg.stack);
          fail('error', msg.message);
        }
      };
      worker.onerror = (e) => {
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
        startWorker();
      });
    }

    const send = (text) => {
      if (!worker) return;
      const bytes = new TextEncoder().encode(text);
      worker.postMessage({ type: 'input', data: btoa(String.fromCharCode(...bytes)) });
    };
    term.onData(send);
    term.onBinary((data) => { if (worker) worker.postMessage({ type: 'input', data: btoa(data) }); });
    term.onResize(() => sendResize());
    let resizeTimer = null;
    window.addEventListener('resize', () => {
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(fitColumns, 100);
    });

    // The app does not ask for mouse tracking, so dragging selects text in xterm as usual and
    // the right-click menu can copy it. Ctrl+C or Cmd+C with a selection copies instead of
    // sending the key.
    term.attachCustomKeyEventHandler((e) => {
      if (e.type === 'keydown' && (e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'c' && term.hasSelection()) {
        if (navigator.clipboard) navigator.clipboard.writeText(term.getSelection()).catch(() => {});
        term.clearSelection();
        return false;
      }
      return true;
    });

    // The wheel becomes the SGR wheel report the app understands, aimed at the cell under the
    // pointer, so the transcript scrolls by three lines per report as it does in a terminal.
    let wheelRemainder = 0;
    container.addEventListener('wheel', (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (!worker) return;
      const screen = container.querySelector('.xterm-screen') || container;
      const box = screen.getBoundingClientRect();
      const col = Math.min(term.cols, Math.max(1, Math.floor((e.clientX - box.left) / (box.width / term.cols)) + 1));
      const row = Math.min(term.rows, Math.max(1, Math.floor((e.clientY - box.top) / (box.height / term.rows)) + 1));
      const step = e.deltaMode === 1 ? 3 : (e.deltaMode === 2 ? 1 : 50);
      wheelRemainder += e.deltaY;
      let reports = '';
      while (wheelRemainder >= step) { reports += `\x1b[<65;${col};${row}M`; wheelRemainder -= step; }
      while (wheelRemainder <= -step) { reports += `\x1b[<64;${col};${row}M`; wheelRemainder += step; }
      if (reports) send(reports);
    }, { capture: true, passive: false });

    // A click anywhere in the box focuses the terminal. In the padding around the rows the
    // default action of the mousedown would move focus to the body right after, so it is
    // stopped there; inside the rows xterm handles the click itself.
    container.addEventListener('mousedown', (e) => {
      if (!e.target.closest('.xterm')) e.preventDefault();
      term.focus();
    });
    container.addEventListener('mouseup', () => term.focus());

    startWorker();
  } catch (err) {
    setStatus('Failed');
    console.error('ilrepl page error', err);
    container.textContent = 'The live session could not start: ' + (err && err.message ? err.message : err);
  }
})();
