import {MOTION} from './motion.mjs';

const STALE_AFTER_MS = 10_000;
const finiteRate = value => typeof value === 'number' && Number.isFinite(value) && value >= 0;
const labels = {waiting:'Waiting for samples', stale:'Readings delayed', no_uplink:'No uplink detected', unavailable:'Unavailable'};

export function formatBandwidth(bps) {
  if (!finiteRate(bps)) return {value:'—',unit:''};
  if (bps > 0 && bps < 10_000) return {value:'<0.01',unit:'Mbps'};
  const mbps = bps / 1_000_000;
  return {value:mbps.toFixed(mbps >= 1000 ? 0 : 2),unit:'Mbps'};
}

export function uplinkReading(record, {connected=true, disconnected=false, elapsedMs=0}={}) {
  if (!connected || disconnected || !record || typeof record !== 'object') return {status:'unavailable'};
  if (record.status !== 'ready') return {status:Object.hasOwn(labels,record.status)?record.status:'unavailable'};
  if (!finiteRate(record.upload_bps) || !finiteRate(record.download_bps)
    || !finiteRate(record.sample_age_ms) || !finiteRate(record.sample_period_ms) || record.sample_period_ms <= 0
    || !Array.isArray(record.ports) || !record.ports.length
    || !record.ports.every(port=>Number.isInteger(port)&&port>0)) return {status:'unavailable'};
  if (record.sample_age_ms + Math.max(0,elapsedMs) >= STALE_AFTER_MS) return {status:'stale'};
  return {status:'ready', upload:record.upload_bps, download:record.download_bps,
    sequence:record.sequence, ports:[...new Set(record.ports)], period:record.sample_period_ms,
    remaining:STALE_AFTER_MS-record.sample_age_ms-Math.max(0,elapsedMs)};
}

export function mountUplinkBandwidth(host, environment=globalThis) {
  const doc=host.ownerDocument, media=environment.matchMedia('(prefers-reduced-motion: reduce)');
  const element=(tag,className,text)=>{const node=doc.createElement(tag);node.className=className;if(text!==undefined)node.textContent=text;return node;};
  const section=element('section','uplink-bandwidth');section.setAttribute('aria-label','Uplink bandwidth');
  const heading=element('div','bandwidth-heading'), title=element('h2','','Uplink bandwidth'), status=element('span','bandwidth-status','Waiting for samples');
  status.setAttribute('role','status');heading.append(title,status);
  const readings=element('dl','bandwidth-readings');
  const fields=['Upload','Download'].map((label,index)=>{
    const item=element('div',`bandwidth-reading ${index?'download':'upload'}`), term=element('dt','');
    const arrow=element('span','bandwidth-direction',index?'↓':'↑');arrow.setAttribute('aria-hidden','true');
    term.append(arrow,element('span','',label));
    const description=element('dd',''), value=element('span','bandwidth-value','—'), unit=element('span','bandwidth-unit','');
    const peak=element('dd','bandwidth-peak');
    description.append(value,unit);item.append(term,description,peak);readings.append(item);
    return {label,description,value,unit,peak};
  });
  const chart=element('div','bandwidth-chart');chart.hidden=true;
  chart.setAttribute('aria-hidden','true');
  const svgNode=tag=>doc.createElementNS('http://www.w3.org/2000/svg',tag);
  const plot=svgNode('svg');plot.setAttribute('viewBox','0 0 240 36');plot.setAttribute('preserveAspectRatio','none');
  const lines=fields.map(field=>{
    const line=svgNode('polyline');line.setAttribute('class',field.label.toLowerCase());
    plot.append(line);return line;
  });
  chart.append(element('span','bandwidth-chart-label','Last minute'),plot);
  section.append(heading,readings,chart);host.append(section);
  function showHistory(history) {
    const samples=Array.isArray(history)?history.filter(point=>Array.isArray(point)&&point.length===3
      &&Number.isFinite(point[0])&&point[0]<=0&&point[0]>-60000&&finiteRate(point[1])&&finiteRate(point[2])):[];
    const peaks=fields.map((field,index)=>Math.max(0,...samples.map(p=>p[index+1])));
    const scale=Math.max(1,...peaks);
    for(const [index,field] of fields.entries()){
      const peak=formatBandwidth(peaks[index]);
      field.peak.textContent=samples.length?`Peak ${peak.value} ${peak.unit}`:'';
      field.peak.title='Highest measured interval in the last minute';
      lines[index].setAttribute('points',samples.map(p=>`${240+p[0]/250},${34-32*p[index+1]/scale}`).join(' '));
    }
    chart.hidden=samples.length<2;
  }
  let record, connected=false, disconnected=false, receivedAt=0, signature='', frame=0, timer=0, disposed=false;
  let displayed=null, animation=null;
  const now=()=>environment.performance.now();
  const cancel=()=>{if(frame)environment.cancelAnimationFrame(frame);frame=0;animation=null;};
  const cancelTimer=()=>{if(timer)environment.clearTimeout(timer);timer=0;};
  const current=()=>uplinkReading(record,{connected,disconnected,elapsedMs:now()-receivedAt});
  const write=values=>{
    displayed=values;
    fields.forEach((field,index)=>{
      const formatted=formatBandwidth(values?.[index]);
      if(field.value.textContent!==formatted.value)field.value.textContent=formatted.value;
      if(field.unit.textContent!==formatted.unit)field.unit.textContent=formatted.unit;
    });
  };
  const paint=time=>{
    frame=0;
    if(disposed||doc.hidden||!animation)return;
    if(current().status!=='ready'){render();return;}
    const progress=Math.max(0,Math.min(1,(time-animation.start)/MOTION.settle));
    const eased=1-(1-progress)**3;
    write(animation.to.map((value,index)=>animation.from[index]+(value-animation.from[index])*eased));
    if(progress<1)frame=environment.requestAnimationFrame(paint);
    else animation=null;
  };
  function render({settle=false}={}) {
    cancelTimer();
    if(disposed||doc.hidden)return;
    const reading=current();section.dataset.status=reading.status;
    if(reading.status!=='ready'){
      cancel();if(signature)showHistory([]);signature='';write(null);status.textContent=labels[reading.status];
      fields.forEach(field=>field.description.setAttribute('aria-label',`${field.label}: ${labels[reading.status]}`));
      section.title='Traffic across detected switch uplinks.';return;
    }
    const seconds=reading.period/1000;
    status.textContent=`${Number(seconds.toFixed(1))} s average · Updates every 5 s`;
    section.title=`${reading.ports.length===1?'Port':'Ports'} ${reading.ports.join(', ')}. Upload leaves the switch; download enters it.`;
    const next=JSON.stringify([reading.sequence,reading.ports,reading.upload,reading.download]);
    const target=[reading.upload,reading.download];
    if(next!==signature||settle){
      signature=next;cancel();showHistory(record.history);
      fields.forEach((field,index)=>{const formatted=formatBandwidth(target[index]);field.description.setAttribute('aria-label',`${field.label}: ${formatted.value} ${formatted.unit}`);});
      if(!displayed||settle||media.matches)write(target);
      else {animation={start:now(),from:[...displayed],to:target};frame=environment.requestAnimationFrame(paint);}
    }
    timer=environment.setTimeout(()=>{timer=0;render();},Math.max(1,reading.remaining));
  }
  const visibility=()=>{if(doc.hidden){cancel();cancelTimer();}else render({settle:true});};
  const motion=()=>{if(media.matches)render({settle:true});};
  doc.addEventListener('visibilitychange',visibility);media.addEventListener('change',motion);
  return {
    update(state, connectionLost=false){
      if(disposed)return;
      if(record!==state?.uplink_bandwidth){record=state?.uplink_bandwidth;receivedAt=now();}
      connected=state?.connected===true;disconnected=connectionLost;render();
    },
    dispose(){disposed=true;cancel();cancelTimer();doc.removeEventListener('visibilitychange',visibility);media.removeEventListener('change',motion);section.remove();}
  };
}
