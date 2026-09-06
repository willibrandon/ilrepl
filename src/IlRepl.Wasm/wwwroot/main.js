// main.js runs on the page. It owns xterm.js and the Web Worker that hosts .NET.
// The script's own URL decides where the worker lives, so the same file works from any base path.
// The terminal is exposed as window.ilreplTerminal so tests can read its size and buffer.

(async () => {
  const scriptUrl = document.currentScript ? document.currentScript.src : new URL('main.js', location.href).href;
  const baseUrl = scriptUrl.substring(0, scriptUrl.lastIndexOf('/') + 1);
  const container = document.getElementById('terminal');
  if (!container) return;
  const statusEl = document.getElementById('session-status');
  const setStatus = (text) => { if (statusEl) statusEl.textContent = text; };
  const rows = 24;
  const fontFamily = '"JetBrains Mono", "Cascadia Code", "Fira Code", Menlo, Monaco, monospace';

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
    container.addEventListener('contextmenu', (e) => e.preventDefault());
    window.ilreplTerminal = term;

    // Only the column count follows the box; the row count stays fixed and the box grows to it.
    const fitColumns = () => {
      const proposed = fit.proposeDimensions();
      const cols = proposed && proposed.cols > 20 ? proposed.cols : 80;
      if (cols !== term.cols) term.resize(cols, rows);
    };
    fitColumns();

    const worker = new Worker(baseUrl + 'worker.js', { type: 'module' });
    const sendResize = () => worker.postMessage({ type: 'resize', cols: term.cols, rows: term.rows });
    // The worker queues this until the runtime is up, so the app's first layout matches the terminal.
    sendResize();

    worker.onmessage = (e) => {
      const msg = e.data;
      if (msg.type === 'output') {
        term.write(new Uint8Array(msg.data));
      } else if (msg.type === 'progress') {
        setStatus(`Loading runtime ${msg.loaded}/${msg.total}`);
      } else if (msg.type === 'workerReady') {
        setStatus('Starting');
        sendResize();
      } else if (msg.type === 'ready') {
        setStatus('Ready');
        sendResize();
        term.focus();
        window.ilreplReady = true;
      } else if (msg.type === 'error') {
        setStatus('Failed');
        term.write('\r\n' + msg.message + '\r\n');
        console.error('ilrepl worker error', msg.message, msg.stack);
      }
    };
    worker.onerror = (e) => {
      setStatus('Failed');
      term.write('\r\nThe worker failed to start: ' + e.message + '\r\n');
      console.error('ilrepl worker error', e.message, e.filename, e.lineno);
    };

    // Mouse buttons are not forwarded: a click only focuses the terminal, so it can never move
    // keyboard focus away from the prompt. Wheel events still reach the app for scrolling.
    const mouseReport = /\x1b\[<(\d+);\d+;\d+[Mm]/g;
    const withoutClicks = (data) => data.replace(mouseReport, (match, button) => ((Number(button) & 64) !== 0 ? match : ''));
    term.onData((data) => {
      const filtered = withoutClicks(data);
      if (filtered.length === 0) return;
      const bytes = new TextEncoder().encode(filtered);
      worker.postMessage({ type: 'input', data: btoa(String.fromCharCode(...bytes)) });
    });
    term.onBinary((data) => worker.postMessage({ type: 'input', data: btoa(data) }));
    term.onResize(() => sendResize());
    let resizeTimer = null;
    window.addEventListener('resize', () => {
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(fitColumns, 100);
    });
    container.addEventListener('mousedown', () => term.focus());
  } catch (err) {
    setStatus('Failed');
    console.error('ilrepl page error', err);
    container.textContent = 'The live session could not start: ' + (err && err.message ? err.message : err);
  }
})();
