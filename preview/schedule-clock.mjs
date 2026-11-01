const anchors=new WeakMap();
const monotonic=()=>globalThis.performance.now();

export function receiveScheduleClock(clock, receivedAt=monotonic()) {
  if(!clock||typeof clock!=='object'||anchors.has(clock))return clock;
  const match=typeof clock.local_iso==='string'&&clock.local_iso.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(\.\d{1,6})?(Z|[+-]\d{2}:\d{2})$/);
  let wall=NaN;
  if(match&&Number.isFinite(Date.parse(clock.local_iso))){
    const civil=`${match[1]}T${match[2]}${(match[3]||'.000').padEnd(4,'0').slice(0,4)}Z`;
    const parsed=Date.parse(civil);
    if(Number.isFinite(parsed)&&new Date(parsed).toISOString()===civil)wall=parsed;
  }
  anchors.set(clock,{wall,receivedAt});return clock;
}

export function hostScheduleDate(state, now=monotonic()) {
  const clock=state?.schedule_clock;
  receiveScheduleClock(clock,now);
  const anchor=clock&&typeof clock==='object'?anchors.get(clock):null;
  const date=new Date(anchor?anchor.wall+Math.max(0,now-anchor.receivedAt):NaN);
  return {
    getHours:()=>date.getUTCHours(),getMinutes:()=>date.getUTCMinutes(),getSeconds:()=>date.getUTCSeconds(),
    getMilliseconds:()=>date.getUTCMilliseconds(),getDay:()=>date.getUTCDay(),getTime:()=>date.getTime(),
  };
}

export function scheduleClockLabel(state) {
  if(!Number.isFinite(hostScheduleDate(state).getTime()))return 'EE computer';
  const label=state.schedule_clock.zone_name;
  return typeof label==='string'&&label.trim()?label.trim():'EE computer';
}
