// The gateway checks private access once when it starts. After a restart or sign-in, Tailscale can still be starting
// at that moment. These states clear without any change in EE, so an enabled setting is checked again on a schedule
// instead of staying off until someone refreshes it by hand. A retry repeats the startup check. When private access is
// on and its port has no Serve configuration, that check adds EE's own route; it never changes other Serve configuration.
export const RETRY_CODES=Object.freeze(['tailscale_not_running','tailscale_unavailable','tailscale_signin_required',
  'private_access_route_unavailable','private_access_listener_failed']);
export const RETRY_DELAYS=Object.freeze([5000,10000,20000,40000,60000,120000,300000]);

export function shouldRetryPrivateAccess(status){
  return status?.enabled===true&&status.ready!==true&&RETRY_CODES.includes(status.error?.code);
}

export function keepPrivateAccessAvailable({manager,active=()=>true,restored=()=>{},setTimer=setTimeout,clearTimer=clearTimeout,delays=RETRY_DELAYS}={}){
  if(typeof manager?.initialize!=='function'||typeof manager?.status!=='function')throw new TypeError('Private access retry requires its manager.');
  if(!Array.isArray(delays)||!delays.length||delays.some(delay=>!Number.isSafeInteger(delay)||delay<1000||delay>3600000))
    throw new RangeError('Choose retry delays from one second to one hour.');
  let timer=null,attempts=0,stopped=false;
  const cancel=()=>{if(timer!==null){clearTimer(timer);timer=null;}};
  function observe(status=manager.status()){
    cancel();
    if(stopped)return status;
    if(!shouldRetryPrivateAccess(status)){attempts=0;return status;}
    timer=setTimer(run,delays[Math.min(attempts,delays.length-1)]);
    timer?.unref?.();
    return status;
  }
  async function run(){
    timer=null;
    if(stopped)return;
    if(active()){
      attempts++;
      const made=attempts;
      try{await manager.initialize();}catch{}
      if(stopped)return;
      if(manager.status().ready===true)restored({attempts:made});
    }
    observe();
  }
  return Object.freeze({observe,stop:()=>{stopped=true;cancel();}});
}
