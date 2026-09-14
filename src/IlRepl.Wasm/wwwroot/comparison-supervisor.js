// The page owns timers and termination, including when a comparison blocks its worker's event loop.
export function createComparisonSupervisor(baseUrl) {
  const active = new Map();
  function cancel(identity) {
    active.get(identity)?.();
  }

  function stop() {
    for (const identity of active.keys()) cancel(identity);
  }

  function run(request, owner) {
    const child = new Worker(baseUrl + 'comparison-worker.js', { type: 'module' });
    const packageData = JSON.parse(request.package);
    const streams = { standardOutput: '', standardError: '' };
    const decoders = { standardOutput: new TextDecoder(), standardError: new TextDecoder() };
    const byte = new Uint8Array(1);
    const append = (stream, text) => {
      streams[stream] = (streams[stream] + text).slice(0, packageData.outputLimit);
    };
    const failure = (outcome, detail) => {
      for (const stream of Object.keys(streams)) append(stream, decoders[stream].decode());
      return JSON.stringify({ outcome, detail, invocations: [], ...streams });
    };
    let finished = false;
    let started = false;
    let timer;
    const finish = (result) => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      child.terminate();
      active.delete(request.identity);
      owner.postMessage({ type: 'comparison-result', identity: request.identity, result });
    };
    active.set(request.identity, () => finish(failure('cancelled', 'comparison cancelled')));
    timer = setTimeout(() => finish(failure('setup-failed', 'comparison runtime startup timed out')), 120000);
    child.onmessage = ({ data }) => {
      if (finished) return;
      if (data.type === 'output' && Object.hasOwn(streams, data.stream)) {
        byte[0] = data.byte;
        append(data.stream, decoders[data.stream].decode(byte, { stream: true }));
      } else if (data.type === 'ready' && !started) {
        started = true;
        clearTimeout(timer);
        timer = setTimeout(() => finish(failure('timeout',
          `execution exceeded ${packageData.timeoutMilliseconds} ms after runtime startup`)), packageData.timeoutMilliseconds);
      } else if (data.type === 'result') {
        finish(data.result);
      } else if (data.type === 'output-limit') {
        finish(failure('output-limit', 'worker output exceeded the configured limit'));
      } else if (data.type === 'failed') {
        finish(failure(started ? 'crashed' : 'setup-failed', data.detail));
      }
    };
    child.onerror = (event) => {
      event.preventDefault();
      finish(failure('crashed', event.message));
    };
    child.postMessage({ package: request.package, original: request.original });
  }

  return { run, cancel, stop };
}
