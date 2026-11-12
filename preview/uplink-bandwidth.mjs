import {MOTION} from './motion.mjs';

const STALE_AFTER_MS = 10_000;
const finiteRate = value => typeof value === 'number' && Number.isFinite(value) && value >= 0;
const labels = {waiting:'Waiting for reading', stale:'Readings delayed', no_uplink:'No uplink detected', unavailable:'Unavailable'};

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

// Own the last measurement with the workspace so changing pages cannot restart its freshness clock.
export function createUplinkMeasurements() {
  let record, workspace, telemetryGeneration, last=null, emptyStatus='waiting', generation=0;
  function clear(status){
    if(last)generation++;
    last=null;emptyStatus=status;
  }
  return {
    get last(){return last;}, get emptyStatus(){return emptyStatus;}, get generation(){return generation;},
    reset(){clear('waiting');workspace=undefined;telemetryGeneration=undefined;record=undefined;},
    update(state, connectionLost, workspaceId, time){
      const incoming=state?.uplink_bandwidth;
      if(workspace!==workspaceId){clear('waiting');telemetryGeneration=undefined;workspace=workspaceId;}
      const token=state?.telemetry?.generation;
      if(token===null||typeof token==='string'){
        if(telemetryGeneration!==undefined&&telemetryGeneration!==token)clear('waiting');
        telemetryGeneration=token;
      }
      if(state?.connected!==true||connectionLost){clear('unavailable');}
      else if(incoming?.status==='no_uplink'||incoming?.status==='waiting'){clear(incoming.status);}
      else if(incoming!==record){
        const ports=Array.isArray(incoming?.ports)?[...new Set(incoming.ports)].sort((a,b)=>a-b):[];
        if(last&&ports.length&&ports.every(p=>Number.isInteger(p)&&p>0)&&last.ports!==ports.join(','))clear('waiting');
        const reading=uplinkReading(incoming);
        if(reading.status==='ready'){
          const key=JSON.stringify([reading.sequence,ports,reading.upload,reading.download]);
          const expires=time+reading.remaining;
          if(last?.key===key)last.expires=Math.min(last.expires,expires);
          else last={key,ports:ports.join(','),reading,history:incoming.history,expires};
        }else{
          emptyStatus=reading.status;
          if(last&&reading.status==='stale')last.expires=Math.min(last.expires,time);
        }
      }
      record=incoming;
    }
  };
}

export function mountUplinkBandwidth(host, environment=globalThis, measurements=createUplinkMeasurements()) {
  const doc=host.ownerDocument, media=environment.matchMedia('(prefers-reduced-motion: reduce)');
  const element=(tag,className,text)=>{const node=doc.createElement(tag);node.className=className;if(text!==undefined)node.textContent=text;return node;};
  const section=element('section','uplink-bandwidth');section.setAttribute('aria-label','Uplink bandwidth');
  const heading=element('div','bandwidth-heading'), title=element('h2','','Uplink bandwidth'), status=element('span','bandwidth-status',labels.waiting);
  status.setAttribute('role','status');heading.append(title,status);
  const readings=element('dl','bandwidth-readings');
  const fields=[['Download','↓',2],['Upload','↑',1]].map(([label,direction,historyColumn])=>{
    const item=element('div',`bandwidth-reading ${label.toLowerCase()}`), term=element('dt','');
    const arrow=element('span','bandwidth-direction',direction);arrow.setAttribute('aria-hidden','true');
    term.append(arrow,element('span','',label));
    const description=element('dd',''), value=element('span','bandwidth-value','—'), unit=element('span','bandwidth-unit','');
    const peak=element('dd','bandwidth-peak');
    description.append(value,unit);item.append(term,description,peak);readings.append(item);
    return {label,description,value,unit,peak,historyColumn};
  });
  const chart=element('div','bandwidth-chart');chart.hidden=true;
  chart.setAttribute('aria-hidden','true');
  const svgNode=tag=>doc.createElementNS('http://www.w3.org/2000/svg',tag);
  const plot=svgNode('svg');plot.setAttribute('viewBox','0 0 240 36');plot.setAttribute('preserveAspectRatio','none');
  const lines=fields.map(field=>{
    const line=svgNode('polyline');line.setAttribute('class',field.label.toLowerCase());
    plot.append(line);return line;
  });
  const chartLabel=element('span','bandwidth-chart-label','Last minute');
  chart.append(chartLabel,plot);
  section.append(heading,readings,chart);host.append(section);
  function showHistory(history, delayed=false) {
    const samples=Array.isArray(history)?history.filter(point=>Array.isArray(point)&&point.length===3
      &&Number.isFinite(point[0])&&point[0]<=0&&point[0]>-60000&&finiteRate(point[1])&&finiteRate(point[2])):[];
    const peaks=fields.map(field=>Math.max(0,...samples.map(p=>p[field.historyColumn])));
    const scale=Math.max(1,...peaks);
    for(const [index,field] of fields.entries()){
      const peak=formatBandwidth(peaks[index]);
      field.peak.textContent=samples.length?`Peak ${peak.value} ${peak.unit}`:'';
      field.peak.title=delayed?'Peak in the minute before the last reading':'Highest measured interval in the last minute';
      lines[index].setAttribute('points',samples.map(p=>`${240+p[0]/250},${34-32*p[field.historyColumn]/scale}`).join(' '));
    }
    chart.hidden=samples.length<2;
  }
  let signature='', frame=0, timer=0, disposed=false, generation=measurements.generation;
  let displayed=null, animation=null, wroteAt=0;
  const now=()=>environment.performance.now();
  const cancel=()=>{if(frame)environment.cancelAnimationFrame(frame);frame=0;animation=null;};
  const cancelTimer=()=>{if(timer)environment.clearTimeout(timer);timer=0;};
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
    if(!measurements.last||time>=measurements.last.expires){render();return;}
    const progress=Math.max(0,Math.min(1,(time-animation.start)/MOTION.settle));
    // Counting up in about twenty steps a second reads the same and avoids a page repaint on every display frame.
    if(progress<1&&time-wroteAt<45){frame=environment.requestAnimationFrame(paint);return;}
    wroteAt=time;
    const eased=1-(1-progress)**3;
    write(animation.to.map((value,index)=>animation.from[index]+(value-animation.from[index])*eased));
    if(progress<1)frame=environment.requestAnimationFrame(paint);
    else animation=null;
  };
  function render({settle=false}={}) {
    cancelTimer();
    if(disposed||doc.hidden)return;
    const {last,emptyStatus}=measurements;
    if(generation!==measurements.generation){generation=measurements.generation;signature='';displayed=null;cancel();}
    if(!last){
      section.dataset.status=emptyStatus;cancel();if(!chart.hidden||fields[0].peak.textContent)showHistory([]);
      write(null);status.hidden=false;status.textContent=labels[emptyStatus];
      fields.forEach(field=>field.description.setAttribute('aria-label',`${field.label}: ${labels[emptyStatus]}`));
      section.title='Traffic across detected switch uplinks.';return;
    }
    const reading=last.reading, remaining=last.expires-now(), delayed=remaining<=0;
    section.dataset.status=delayed?'stale':'ready';
    status.hidden=!delayed;status.textContent=delayed?'Last reading':'';
    chartLabel.textContent=delayed?'Minute before last reading':'Last minute';
    section.title=`${reading.ports.length===1?'Port':'Ports'} ${reading.ports.join(', ')}. Upload leaves the switch; download enters it.`;
    const next=JSON.stringify([last.key,delayed]);
    const target=[reading.download,reading.upload];
    if(next!==signature||settle){
      signature=next;cancel();showHistory(last.history,delayed);
      fields.forEach((field,index)=>{const formatted=formatBandwidth(target[index]);field.description.setAttribute('aria-label',`${field.label}: ${formatted.value} ${formatted.unit}${delayed?' (last reading)':''}`);});
      if(!displayed||settle||delayed||media.matches)write(target);
      else {animation={start:now(),from:[...displayed],to:target};frame=environment.requestAnimationFrame(paint);}
    }
    if(!delayed)timer=environment.setTimeout(()=>{timer=0;render();},Math.max(1,remaining));
  }
  const visibility=()=>{if(doc.hidden){cancel();cancelTimer();}else render({settle:true});};
  const motion=()=>{if(media.matches)render({settle:true});};
  doc.addEventListener('visibilitychange',visibility);media.addEventListener('change',motion);
  return {
    update(state, connectionLost=false, workspaceId){
      if(disposed)return;
      measurements.update(state,connectionLost,workspaceId,now());render();
    },
    dispose(){disposed=true;cancel();cancelTimer();doc.removeEventListener('visibilitychange',visibility);media.removeEventListener('change',motion);section.remove();}
  };
}
