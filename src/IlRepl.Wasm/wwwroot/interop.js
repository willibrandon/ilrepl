// interop.js runs inside the Web Worker. .NET imports these functions with [JSImport].

const inputChunks = [];
let pendingResize = '';

export function postTerminalOutput(data) {
  // Copy out of WASM memory before posting; structured cloning a view would clone the whole heap.
  const copy = new Uint8Array(data.length);
  copy.set(data);
  self.postMessage({ type: 'output', data: copy.buffer }, [copy.buffer]);
}

export function notifyReady(cols, rows) {
  self.postMessage({ type: 'ready', cols, rows });
}

export function pollAllInput() {
  if (inputChunks.length === 0) return null;
  let total = 0;
  for (const chunk of inputChunks) total += chunk.length;
  const result = new Uint8Array(total);
  let offset = 0;
  for (const chunk of inputChunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  inputChunks.length = 0;
  return result;
}

export function pollResize() {
  const r = pendingResize;
  pendingResize = '';
  return r;
}

// Messages that arrive before this module is imported are queued by worker.js and replayed here.
const queued = self.__ilreplQueuedMessages || [];
self.__ilreplQueuedMessages = null;

self.onmessage = (e) => {
  const msg = e.data;
  if (msg.type === 'input') {
    inputChunks.push(Uint8Array.from(atob(msg.data), (c) => c.charCodeAt(0)));
    if (self.__ilreplSignalInput) self.__ilreplSignalInput();
  } else if (msg.type === 'resize') {
    pendingResize = msg.cols + ',' + msg.rows;
    if (self.__ilreplSignalInput) self.__ilreplSignalInput();
  }
};

for (const msg of queued) {
  self.onmessage({ data: msg });
}
