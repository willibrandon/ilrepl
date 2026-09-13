// Imported exclusively by the dedicated comparison runtime.
export function ready() {
  self.postMessage({ type: 'ready' });
}

export function outputLimit() {
  self.postMessage({ type: 'output-limit' });
}
