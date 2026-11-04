export async function registerPwa(environment = globalThis) {
  if (!environment.isSecureContext || !environment.navigator?.serviceWorker) return null;
  return environment.navigator.serviceWorker.register('/preview/sw.mjs', {
    type:'module', scope:'/', updateViaCache:'none'
  });
}

if (typeof window !== 'undefined') {
  const register = () => { void registerPwa().catch(() => {}); };
  if (document.readyState === 'complete') register();
  else window.addEventListener('load',register,{once:true});
}
