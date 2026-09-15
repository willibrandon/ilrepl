import { dotnet } from './_framework/dotnet.js';

// Each worker accepts one package and exits through the page's supervisor.
self.onmessage = async ({ data }) => {
  self.onmessage = null;
  try {
    const limit = JSON.parse(data.package).outputLimit;
    const streams = { standardOutput: '', standardError: '' };
    const decoders = { standardOutput: new TextDecoder(), standardError: new TextDecoder() };
    const encoder = new TextEncoder();
    const byte = new Uint8Array(1);
    let exceeded = false;
    const output = (stream, text) => {
      if (exceeded) return;
      const next = streams[stream] + text;
      const captured = text.slice(0, Math.max(0, limit - streams[stream].length));
      streams[stream] += captured;
      if (next.length > limit) {
        exceeded = true;
        self.postMessage({ type: 'output-limit' });
      }
    };
    const outputByte = (stream, value) => {
      if (exceeded) return;
      byte[0] = value;
      self.postMessage({ type: 'output', stream, byte: byte[0] });
      output(stream, decoders[stream].decode(byte, { stream: true }));
    };
    const outputLine = (stream, text) => {
      for (const value of encoder.encode(text + '\n')) outputByte(stream, value);
    };
    const runtime = await dotnet
      .withModuleConfig({
        stdout: (value) => outputByte('standardOutput', value),
        stderr: (value) => outputByte('standardError', value),
        print: (text) => outputLine('standardOutput', text),
        printErr: (text) => outputLine('standardError', text),
        onAbort: (reason) => self.postMessage({ type: 'failed', detail: String(reason) }),
        onExit: (code) => self.postMessage({ type: 'failed', detail: `comparison runtime exited with code ${code}` })
      })
      .create();
    runtime.Module.FS.mkdirTree('/work');
    runtime.Module.FS.chdir('/work');
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    const json = await exports.IlRepl.Wasm.BrowserComparisonWorker.RunAsync(data.package, data.original);
    const result = JSON.parse(json);
    for (const stream of Object.keys(streams)) {
      output(stream, decoders[stream].decode());
      result[stream] = streams[stream];
    }
    if (exceeded) return;
    self.postMessage({ type: 'result', result: JSON.stringify(result) });
  } catch (error) {
    self.postMessage({ type: 'failed', detail: String(error) });
  }
};
