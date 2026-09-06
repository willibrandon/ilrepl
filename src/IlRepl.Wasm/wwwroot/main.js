// main.js runs on the page. It owns xterm.js and the Web Worker that hosts .NET.
// The script's own URL decides where the worker lives, so the same file works from any base path.
// The terminal is exposed as window.ilreplTerminal so tests can read its size and buffer.

(function () {
  const scriptUrl = document.currentScript ? document.currentScript.src : new URL('main.js', location.href).href;
  const baseUrl = scriptUrl.substring(0, scriptUrl.lastIndexOf('/') + 1);

  let term = null;
  let worker = null;
  let ready = false;

  function sendResize() {
    if (!term || !worker) return;
    worker.postMessage({ type: 'resize', cols: term.cols, rows: term.rows });
  }

  function init() {
    const container = document.getElementById('terminal');
    if (!container) return;

    term = new Terminal({
      cursorBlink: true,
      convertEol: false,
      allowProposedApi: true,
      fontSize: 14,
      fontFamily: 'Menlo, Monaco, "Courier New", monospace',
      theme: { background: '#1e1e1e', foreground: '#d4d4d4' },
    });
    const fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(container);
    fitAddon.fit();
    term.write('Loading the .NET runtime...\r\n');
    window.ilreplTerminal = term;

    // The worker's message handler is installed once the runtime has imported interop.js, so the
    // size is sent on every message from the worker until the app reports ready.
    worker = new Worker(baseUrl + 'worker.js', { type: 'module' });

    worker.onmessage = (e) => {
      const msg = e.data;
      if (msg.type === 'output') {
        term.write(new Uint8Array(msg.data));
      } else if (msg.type === 'workerReady') {
        sendResize();
      } else if (msg.type === 'ready') {
        ready = true;
        sendResize();
        term.focus();
        window.ilreplReady = true;
      } else if (msg.type === 'error') {
        term.write('\r\n' + msg.message + '\r\n');
        console.error('ilrepl worker error', msg.message, msg.stack);
      }
    };
    worker.onerror = (e) => {
      term.write('\r\nThe worker failed to start: ' + e.message + '\r\n');
      console.error('ilrepl worker error', e.message, e.filename, e.lineno);
    };

    term.onData((data) => {
      const bytes = new TextEncoder().encode(data);
      worker.postMessage({ type: 'input', data: btoa(String.fromCharCode(...bytes)) });
    });
    term.onBinary((data) => {
      worker.postMessage({ type: 'input', data: btoa(data) });
    });

    let resizeTimer = null;
    term.onResize(() => {
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(() => {
        sendResize();
        resizeTimer = null;
      }, 100);
    });
    window.addEventListener('resize', () => fitAddon.fit());
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
