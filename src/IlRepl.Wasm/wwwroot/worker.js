// worker.js boots the .NET WebAssembly runtime inside a Web Worker so the page stays responsive.
import { dotnet } from './_framework/dotnet.js';

// Queue page messages until interop.js installs the real handler, so an early resize is not lost.
self.__ilreplQueuedMessages = [];
self.onmessage = (e) => {
  if (self.__ilreplQueuedMessages) self.__ilreplQueuedMessages.push(e.data);
};

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
  self.postMessage({ type: 'workerReady' });
  const config = getConfig();
  const exports = await getAssemblyExports(config.mainAssemblyName);
  self.__ilreplSignalInput = exports.IlRepl.Wasm.WasmPresentationAdapter.SignalInputAvailable;
  await runMain();
} catch (err) {
  self.postMessage({ type: 'error', message: err.toString(), stack: err.stack });
}
