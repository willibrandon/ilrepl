// The validation bundle alone includes this worker. Every image gets an independent Mono runtime.
import { dotnet } from './_framework/dotnet.js';

self.onmessage = async ({ data }) => {
  self.onmessage = null;
  try {
    const runtime = await dotnet.create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    const execute = exports.IlRepl.Wasm.BrowserConformance?.Execute;
    if (!execute) throw new Error('The browser conformance entry point is absent. Publish with --conformance.');
    self.postMessage({ result: execute(data.image, data.type, data.method) });
  } catch (error) {
    self.postMessage({ error: String(error), stack: error.stack });
  }
};
