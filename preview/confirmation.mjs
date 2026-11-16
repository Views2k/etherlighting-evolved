import {randomId} from './dom.mjs';

const pendingConfirmations = new WeakMap();

export function confirmAction(owner, title, message, accept, signal) {
  if (signal?.aborted) return Promise.resolve(false);
  const pending = pendingConfirmations.get(owner);
  if (pending) return pending.title === title && pending.message === message && pending.accept === accept && pending.signal === signal
    ? pending.promise : Promise.resolve(false);
  const promise = new Promise(resolve => {
    const previousFocus = document.activeElement;
    const dialog = document.createElement('dialog'); dialog.className = 'dialog';
    const shell = document.createElement('div'); shell.className = 'dialog-sheet';
    const heading = document.createElement('h2'); heading.textContent = title;
    heading.id = `confirmation-${randomId()}`;
    dialog.setAttribute('aria-labelledby', heading.id);
    const copy = document.createElement('p'); copy.className = 'muted'; copy.textContent = message;
    copy.id = `${heading.id}-description`; dialog.setAttribute('aria-describedby',copy.id);
    const actions = document.createElement('div'); actions.className = 'view-actions';
    let closed = false;
    const close = result => {
      if (closed) return;
      closed = true; pendingConfirmations.delete(owner); dialog.close(); dialog.remove(); signal?.removeEventListener('abort', abort);
      if (owner.isConnected && previousFocus?.isConnected && !previousFocus.disabled) previousFocus.focus({preventScroll:true});
      resolve(result);
    };
    const abort = () => close(false);
    for (const [label, result] of [['Cancel',false],[accept,true]]) {
      const control = document.createElement('button'); control.type = 'button';
      control.className = `button${result ? ' primary' : ''}`; control.textContent = label;
      control.addEventListener('click', () => close(result)); actions.append(control);
    }
    shell.append(heading,copy,actions); dialog.append(shell); document.body.append(dialog);
    dialog.addEventListener('cancel', event => {event.preventDefault();close(false);});
    signal?.addEventListener('abort', abort, {once:true});
    dialog.showModal(); actions.firstElementChild.focus();
  });
  pendingConfirmations.set(owner, {title, message, accept, signal, promise});
  return promise;
}
