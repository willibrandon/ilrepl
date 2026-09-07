// worker.js boots the .NET WebAssembly runtime inside a Web Worker so the page stays responsive.
import { dotnet } from './_framework/dotnet.js';

// Queue page messages until interop.js installs the real handler, so an early resize is not lost.
self.__ilreplQueuedMessages = [];
self.onmessage = (e) => {
  if (self.__ilreplQueuedMessages) self.__ilreplQueuedMessages.push(e.data);
};

// The page watches for these. They stop when the event loop is blocked, which is how a cell that
// never returns is noticed from outside.
setInterval(() => self.postMessage({ type: 'heartbeat' }), 1000);

try {
  let loaded = 0;
  let total = 0;
  const runtime = await dotnet
    .withResourceLoader((type, name, defaultUri, integrity, behavior) => {
      // The runtime imports its own JavaScript modules; only the assets it downloads are counted.
      if (type === 'dotnetjs') return undefined;
      total++;
      const response = fetch(defaultUri, { cache: 'default', integrity: integrity ?? undefined });
      response.then(() => {
        loaded++;
        self.postMessage({ type: 'progress', loaded, total });
      }).catch(() => {});
      return response;
    })
    .create();
  const { getAssemblyExports, getConfig, runMain } = runtime;

  // The Greeter sample goes into the runtime's in-memory file system, where .load finds it by path.
  try {
    const sample = await fetch('samples/Greeter.dll', { cache: 'default' });
    if (sample.ok) {
      runtime.Module.FS.mkdirTree('/samples');
      runtime.Module.FS.writeFile('/samples/Greeter.dll', new Uint8Array(await sample.arrayBuffer()));
    }
  } catch (err) {
    console.warn('the Greeter sample is not available in this session', err);
  }

  self.postMessage({ type: 'workerReady' });
  const config = getConfig();
  const exports = await getAssemblyExports(config.mainAssemblyName);
  self.__ilreplSignalInput = exports.IlRepl.Wasm.WasmPresentationAdapter.SignalInputAvailable;
  await runMain();
} catch (err) {
  self.postMessage({ type: 'error', message: err.toString(), stack: err.stack });
}
