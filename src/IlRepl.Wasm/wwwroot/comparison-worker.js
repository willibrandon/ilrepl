import { dotnet } from './_framework/dotnet.js';

// Each worker accepts one package and exits through the page's supervisor.
self.onmessage = async ({ data }) => {
  self.onmessage = null;
  try {
    const limit = JSON.parse(data.package).outputLimit;
    const streams = { standardOutput: '', standardError: '' };
    const output = (stream, text) => {
      const next = streams[stream] + text + '\n';
      streams[stream] = next.slice(0, limit);
      if (next.length > limit) self.postMessage({ type: 'output-limit' });
    };
    const runtime = await dotnet
      .withModuleConfig({
        print: (text) => output('standardOutput', text),
        printErr: (text) => output('standardError', text),
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
      result[stream] += streams[stream];
      if (result[stream].length > limit) {
        self.postMessage({ type: 'output-limit' });
        return;
      }
    }
    self.postMessage({ type: 'result', result: JSON.stringify(result) });
  } catch (error) {
    self.postMessage({ type: 'failed', detail: String(error) });
  }
};
