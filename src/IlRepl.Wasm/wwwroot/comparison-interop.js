// Imported exclusively by the dedicated comparison runtime.
export function ready() {
  self.postMessage({ type: 'ready' });
}

export function outputLimit() {
  self.postMessage({ type: 'output-limit' });
}

export function restoreFileTime(path, milliseconds) {
  const filesystem = globalThis.getDotnetRuntime(0).Module.FS;
  const node = filesystem.lookupPath(path, { follow: false }).node;
  // This runtime's memory filesystem stores one timestamp for all three time fields.
  node.node_ops.setattr(node, { timestamp: milliseconds });
}
