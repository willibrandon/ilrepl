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

export function notifyExited() {
  self.postMessage({ type: 'exited' });
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

// History lives in IndexedDB, which a worker can open: one record per entry under an
// auto-incrementing key, so two tabs never write over each other, and the oldest records beyond
// a thousand go in the same transaction that adds a new one. A missing database is a first visit,
// not an error; a database that cannot be opened rejects, and the app says so once.
const historyLimit = 1000;

function openHistory() {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === 'undefined' || !indexedDB) {
      reject(new Error('IndexedDB is not available'));
      return;
    }
    const request = indexedDB.open('ilrepl', 1);
    request.onupgradeneeded = () => {
      request.result.createObjectStore('history', { autoIncrement: true });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error('the history database could not be opened'));
    request.onblocked = () => reject(new Error('the history database is blocked by another tab'));
  });
}

export async function loadHistory() {
  const db = await openHistory();
  try {
    return await new Promise((resolve, reject) => {
      const tx = db.transaction('history', 'readonly');
      const request = tx.objectStore('history').getAll();
      // One JSON array crosses to .NET, so any character an entry holds comes through as itself.
      request.onsuccess = () => resolve(JSON.stringify(request.result.map((e) => String(e))));
      request.onerror = () => reject(request.error || new Error('the history could not be read'));
    });
  } finally {
    db.close();
  }
}

export async function appendHistory(entry) {
  const db = await openHistory();
  try {
    await new Promise((resolve, reject) => {
      const tx = db.transaction('history', 'readwrite');
      const store = tx.objectStore('history');
      store.add(entry);
      const count = store.count();
      count.onsuccess = () => {
        let extra = count.result - historyLimit;
        if (extra <= 0) return;
        const cursor = store.openCursor();
        cursor.onsuccess = () => {
          const c = cursor.result;
          if (!c || extra <= 0) return;
          c.delete();
          extra--;
          c.continue();
        };
      };
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error || new Error('the history could not be written'));
      tx.onabort = () => reject(tx.error || new Error('the history write was abandoned'));
    });
  } finally {
    db.close();
  }
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
