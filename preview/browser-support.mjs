import {lanPage} from './lan-address.mjs';

export function supportedBrowser(env) {
  return typeof env.structuredClone === 'function'
    && typeof env.Object.hasOwn === 'function'
    && typeof env.Array.prototype.at === 'function'
    && typeof env.AbortSignal === 'function' && typeof env.AbortSignal.timeout === 'function'
    && Boolean(env.crypto) && typeof env.crypto.getRandomValues === 'function'
    && typeof env.HTMLDialogElement === 'function' && typeof env.HTMLDialogElement.prototype.showModal === 'function'
    && Boolean(env.CSS) && typeof env.CSS.supports === 'function'
    && env.CSS.supports('color','color(srgb 1 0 0)')
    && env.CSS.supports('color','color-mix(in srgb, black, white)') && env.CSS.supports('selector(:has(*))');
}

// A LAN access address is plain HTTP by design; any other page that is not a secure context was opened by a name or
// address EE does not serve.
export function browserRecoveryMessage(env) {
  if (env.isSecureContext === false && !lanPage(env.location)) return 'Open EE on this computer using localhost, or use the address shown in Settings on the computer running EE.';
  return supportedBrowser(env) ? null : 'Update to a current version of Edge, Chrome, Firefox, or Safari, then open this page again.';
}
