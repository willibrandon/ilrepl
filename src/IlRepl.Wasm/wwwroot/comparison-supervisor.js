// The page owns timers and termination, including when a comparison blocks its worker's event loop.
export function createComparisonSupervisor(baseUrl) {
  const active = new Map();
  const failure = (outcome, detail) => JSON.stringify({
    outcome, detail, invocations: [], standardOutput: '', standardError: ''
  });

  function cancel(identity) {
    active.get(identity)?.(failure('cancelled', 'comparison cancelled'));
  }

  function stop() {
    for (const identity of active.keys()) cancel(identity);
  }

  function run(request, owner) {
    const child = new Worker(baseUrl + 'comparison-worker.js', { type: 'module' });
    const packageData = JSON.parse(request.package);
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
    active.set(request.identity, finish);
    timer = setTimeout(() => finish(failure('setup-failed', 'comparison runtime startup timed out')), 120000);
    child.onmessage = ({ data }) => {
      if (data.type === 'ready' && !started) {
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
