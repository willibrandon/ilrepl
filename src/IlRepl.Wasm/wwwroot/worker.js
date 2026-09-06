// worker.js boots the .NET WebAssembly runtime inside a Web Worker so the page stays responsive.
import { dotnet } from './_framework/dotnet.js';

// Queue page messages until interop.js installs the real handler, so an early resize is not lost.
self.__ilreplQueuedMessages = [];
self.onmessage = (e) => {
  if (self.__ilreplQueuedMessages) self.__ilreplQueuedMessages.push(e.data);
};

try {
  const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
  self.postMessage({ type: 'workerReady' });
  const config = getConfig();
  const exports = await getAssemblyExports(config.mainAssemblyName);
  self.__ilreplSignalInput = exports.IlRepl.Wasm.WasmPresentationAdapter.SignalInputAvailable;
  await runMain();
} catch (err) {
  self.postMessage({ type: 'error', message: err.toString(), stack: err.stack });
}
