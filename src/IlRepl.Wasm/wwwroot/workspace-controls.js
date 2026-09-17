// File controls live on the page; only the shared .NET codec reads or writes session documents.
export function createWorkspaceControls({ getWorker, getCheckpoint, replace, setStatus, focus }) {
  const pending = new Map();
  let sequence = 0;
  const toolbar = document.getElementById('session-files');
  const terminal = document.getElementById('terminal');
  const message = document.createElement('span');
  message.id = 'session-file-message';
  message.setAttribute('role', 'status');
  const picker = document.createElement('input');
  picker.type = 'file';
  picker.accept = '.ilrepl.json,application/json';
  picker.hidden = true;
  toolbar.append(picker);

  function request(operation, value = '') {
    const worker = getWorker();
    if (!worker) return Promise.reject(new Error('The session is still starting.'));
    return new Promise((resolve, reject) => {
      const identity = ++sequence;
      const timeout = setTimeout(() => {
        pending.delete(identity);
        reject(new Error('The execution runtime did not respond; wait for the current operation or restart the session.'));
      }, 30_000);
      pending.set(identity, { worker, resolve, reject, timeout });
      worker.postMessage({ type: 'workspace-request', identity, operation, value });
    });
  }

  function download(source, path = 'session.ilrepl.json') {
    const url = URL.createObjectURL(new Blob([source], { type: 'application/json' }));
    const link = document.createElement('a');
    link.href = url;
    const name = path.split(/[\\/]/).pop() || 'session.ilrepl.json';
    link.download = /\.ilrepl\.json$/i.test(name) ? name : name + '.ilrepl.json';
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
    message.textContent = 'Session downloaded.';
  }

  async function save() {
    const source = await request('capture');
    const name = getCheckpoint()?.path || 'session.ilrepl.json';
    download(source, name);
    await request('saved', name);
  }

  async function canReplace() {
    await request('capture');
    if (!getCheckpoint()?.dirty) return true;
    const dialog = document.createElement('dialog');
    const question = document.createElement('p');
    question.textContent = 'Save changes before opening another session?';
    dialog.append(question);
    document.body.append(dialog);
    const choice = await new Promise(resolve => {
      for (const label of ['Save', 'Discard', 'Cancel']) {
        const button = document.createElement('button');
        button.textContent = label;
        button.onclick = () => { dialog.close(); resolve(label); };
        dialog.append(button);
      }
      dialog.addEventListener('keydown', event => {
        if (!['ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault();
        const choices = Array.from(dialog.querySelectorAll('button'));
        const current = choices.indexOf(document.activeElement);
        choices[(current + (event.key === 'ArrowDown' ? 1 : choices.length - 1)) % choices.length].focus();
      });
      dialog.addEventListener('cancel', () => resolve('Cancel'), { once: true });
      dialog.showModal();
    });
    dialog.remove();
    if (choice === 'Save') await save();
    if (choice === 'Cancel') focus();
    return choice !== 'Cancel';
  }

  const showError = error => { message.textContent = String(error.message || error); };
  picker.addEventListener('change', async () => {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) return;
    try {
      if (file.size > 8 * 1024 * 1024) {
        throw new Error('Browser session files are limited to 8 MiB. Open larger files with desktop ilrepl.');
      }
      const source = await request('validate', await file.text());
      if (await canReplace()) replace(source, file.name);
    } catch (error) { showError(error); }
  });

  async function share() {
    let url;
    try {
      url = await request('share', location.href.split('#')[0]);
    } catch (error) {
      if (!String(error).includes('download the session file')) throw error;
      await save();
      message.textContent = 'The example is too large for a link. The session file was downloaded.';
      return;
    }
    try {
      await navigator.clipboard.writeText(url);
      message.textContent = 'Share link copied. Opening it restores source without running it.';
    } catch {
      const field = document.createElement('input');
      field.readOnly = true;
      field.value = url;
      field.setAttribute('aria-label', 'Session share link');
      message.replaceChildren('Copy this link: ', field);
      field.focus();
      field.select();
    }
  }

  const actions = [
    ['Open', 'Open a saved session without running it.', () => picker.click()],
    ['Download', 'Download the current session as an .ilrepl.json file.', save],
    ['Share', 'Copy a link to share this session.', share],
    ['Run all', 'Run all saved cells from the beginning.', async () => replace(await request('capture'), getCheckpoint()?.path, '')],
  ];
  for (const [label, tooltip, action] of actions) {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.title = tooltip;
    button.id = 'session-' + label.toLowerCase().replace(' ', '-');
    button.onclick = () => Promise.resolve().then(action).catch(showError);
    toolbar.append(button);
  }
  toolbar.append(message);

  terminal.addEventListener('keydown', event => {
    if (!(event.ctrlKey || event.metaKey) || event.altKey || !['s', 'o'].includes(event.key.toLowerCase())) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (event.key.toLowerCase() === 'o') picker.click();
    else save().catch(showError);
  }, true);

  return {
    request,
    stop(worker) {
      for (const [identity, waiter] of pending) {
        if (waiter.worker !== worker) continue;
        pending.delete(identity);
        clearTimeout(waiter.timeout);
        waiter.reject(new Error('The execution runtime was replaced.'));
      }
    },
    message(msg, worker) {
      if (msg.type === 'workspace-result') {
        const waiter = pending.get(msg.identity);
        if (waiter?.worker !== worker) return;
        pending.delete(msg.identity);
        clearTimeout(waiter.timeout);
        if (msg.error) waiter.reject(new Error(msg.error));
        else waiter.resolve(msg.value);
      } else if (msg.type === 'workspace-action') {
        try {
          if (msg.operation === 'download') download(msg.document, msg.value);
          worker.postMessage({ type: 'workspace-ack', identity: msg.identity });
          if (msg.operation === 'run') {
            message.textContent = 'Starting a fresh runtime to run the saved source.';
            replace(msg.document, getCheckpoint()?.path, msg.value);
          }
        } catch (error) {
          worker.postMessage({ type: 'workspace-ack', identity: msg.identity, error: String(error) });
        }
      }
    },
    ready() { message.textContent = ''; setStatus('Ready'); focus(); },
    running() { message.textContent = 'Running the saved source. Restart stops an unresponsive cell.'; },
    completed() { message.textContent = 'Execution finished. Results are shown in the terminal.'; },
    error: showError,
  };
}
