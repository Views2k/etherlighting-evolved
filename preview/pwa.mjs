export async function registerPwa(environment = globalThis) {
  if (!environment.isSecureContext || !environment.navigator?.serviceWorker) return null;
  return environment.navigator.serviceWorker.register('/preview/sw.mjs', {
    type:'module', scope:'/', updateViaCache:'none'
  });
}

if (typeof window !== 'undefined') {
  if (window.matchMedia('(display-mode: standalone)').matches) {
    const root = document.documentElement;
    const updateBottomInset = () => {
      // Some Home Screen viewports stop one status-bar height above the screen bottom.
      const topInset = parseFloat(getComputedStyle(root).getPropertyValue('--standalone-safe-top'));
      const bottomGap = window.screen.height - window.screenY - window.innerHeight;
      root.classList.toggle('reserved-screen-bottom', window.innerWidth === window.screen.width
        && topInset > 0 && Math.abs(bottomGap - topInset) <= 1);
    };
    updateBottomInset();
    window.addEventListener('resize', updateBottomInset);
    window.addEventListener('pageshow', updateBottomInset);
  }

  const register = () => { void registerPwa().catch(() => {}); };
  if (document.readyState === 'complete') register();
  else window.addEventListener('load',register,{once:true});
}
