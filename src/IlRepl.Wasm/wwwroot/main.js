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

  if (!document.querySelector('link[href*="xterm"]')) {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = 'https://esm.sh/@xterm/xterm@5.5.0/css/xterm.css';
    document.head.appendChild(link);
    await new Promise((resolve) => { link.onload = resolve; link.onerror = resolve; });
  }

  const { Terminal } = await import('https://esm.sh/@xterm/xterm@5.5.0');
  const { Unicode11Addon } = await import('https://esm.sh/@xterm/addon-unicode11@0.8.0');

  // A fixed size that matches what the app lays out for; the box is sized by the terminal.
  const term = new Terminal({
    cols: 100,
    rows: 28,
    cursorBlink: true,
    cursorStyle: 'block',
    fontSize: 14,
    fontFamily: '"JetBrains Mono", "Cascadia Code", "Fira Code", Menlo, Monaco, monospace',
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
  const unicode = new Unicode11Addon();
  term.loadAddon(unicode);
  term.unicode.activeVersion = '11';
  term.open(container);
  container.addEventListener('contextmenu', (e) => e.preventDefault());
  window.ilreplTerminal = term;

  const worker = new Worker(baseUrl + 'worker.js', { type: 'module' });
  const sendResize = () => worker.postMessage({ type: 'resize', cols: term.cols, rows: term.rows });

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

  term.onData((data) => {
    const bytes = new TextEncoder().encode(data);
    worker.postMessage({ type: 'input', data: btoa(String.fromCharCode(...bytes)) });
  });
  term.onBinary((data) => worker.postMessage({ type: 'input', data: btoa(data) }));
  term.onResize(() => sendResize());
  container.addEventListener('click', () => term.focus());
})();
