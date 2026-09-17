// interop.js runs inside the Web Worker. .NET imports these functions with [JSImport].

const inputChunks = [];
let pendingResize = '';
const comparisons = new Map();
const acknowledgements = new Map();
let checkpointSequence = 0;
let initialSource = null;
let initialFile = null;
let preferences = 'true,false';
let announceOpen = false;

export function initialDocument() {
  const source = initialSource;
  initialSource = null;
  return source;
}

export function initialPath() { return initialFile; }
export function initialPreferences() { return preferences; }
export function initialAnnounceOpen() { return announceOpen; }

export function checkpoint(source, path, dirty, echoStack, showTiming, pendingSubmission, pendingSource,
  entryPrefix, cellNumbers, assetHashes) {
  return acknowledged({ type: 'workspace-checkpoint', document: source, path, dirty, entryPrefix,
    cellNumbers: cellNumbers ? cellNumbers.split(',').map(Number) : [], assetHashes: assetHashes ? assetHashes.split(',') : [],
    preferences: [echoStack, showTiming].join(','), pendingSubmission, pendingSource: JSON.parse(pendingSource) });
}

export function editorChanged(editor) {
  self.postMessage({ type: 'workspace-editor', editor });
}

export function inputBarrier() {
  inputChunks.push(new TextEncoder().encode('\x1b[24;8~'));
  self.__ilreplSignalInput?.();
}

export function pageAction(operation, document, value) {
  return acknowledged({ type: 'workspace-action', operation, document, value });
}

function acknowledged(message) {
  return new Promise((resolve, reject) => {
    const identity = ++checkpointSequence;
    acknowledgements.set(identity, { resolve, reject });
    self.postMessage({ ...message, identity });
  });
}

export function runComparisonSide(identity, packageJson, original) {
  return new Promise((resolve) => {
    comparisons.set(identity, resolve);
    self.postMessage({ type: 'comparison-run', identity, package: packageJson, original });
  });
}

export function cancelComparisonSide(identity) {
  self.postMessage({ type: 'comparison-cancel', identity });
}

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

export function documentationTarget(url, sequence, active) {
  self.postMessage({ type: 'documentation-target', url, sequence, active });
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
  } else if (msg.type === 'comparison-result') {
    const resolve = comparisons.get(msg.identity);
    comparisons.delete(msg.identity);
    resolve?.(msg.result);
  } else if (msg.type === 'workspace-init') {
    initialSource = msg.document || null;
    initialFile = msg.path || null;
    preferences = msg.preferences || 'true,false';
    announceOpen = msg.announceOpen === true;
  } else if (msg.type === 'workspace-ack') {
    const pending = acknowledgements.get(msg.identity);
    acknowledgements.delete(msg.identity);
    if (msg.error) pending?.reject(new Error(msg.error));
    else pending?.resolve();
  } else if (msg.type === 'workspace-request') {
    Promise.resolve().then(() => self.__ilreplWorkspace(msg.operation, msg.value || '')).then(
      value => self.postMessage({ type: 'workspace-result', identity: msg.identity, value }),
      error => self.postMessage({ type: 'workspace-result', identity: msg.identity, error: String(error) }));
  }
};

for (const msg of queued) {
  self.onmessage({ data: msg });
}
