export function supportedBrowser(env) {
  return typeof env.structuredClone === 'function'
    && typeof env.Object.hasOwn === 'function'
    && typeof env.Array.prototype.at === 'function'
    && typeof env.AbortSignal === 'function' && typeof env.AbortSignal.timeout === 'function'
    && Boolean(env.crypto) && typeof env.crypto.randomUUID === 'function'
    && typeof env.HTMLDialogElement === 'function' && typeof env.HTMLDialogElement.prototype.showModal === 'function'
    && Boolean(env.CSS) && typeof env.CSS.supports === 'function'
    && env.CSS.supports('color','color(srgb 1 0 0)')
    && env.CSS.supports('color','color-mix(in srgb, black, white)') && env.CSS.supports('selector(:has(*))');
}

export function browserRecoveryMessage(env) {
  if (env.isSecureContext === false) return 'Open EE on this computer using localhost, or use its private HTTPS address from Settings → Mobile & remote access.';
  return supportedBrowser(env) ? null : 'Update to a current version of Edge, Chrome, Firefox, or Safari, then open this page again.';
}
