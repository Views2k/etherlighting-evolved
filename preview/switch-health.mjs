const finite = value => typeof value === 'number' && Number.isFinite(value) && value >= 0;
const number = (value, digits = 1) => finite(value) ? value.toLocaleString('en-US', {maximumFractionDigits:digits}) : '—';
const bytes = value => {
  if (!finite(value)) return '—';
  const scale=value>=1024**3?1024**3:1024**2,unit=scale===1024**3?'GiB':'MiB';
  return `${number(value/scale)} ${unit}`;
};
const usage = value => value && finite(value.used_bytes) && finite(value.total_bytes) && value.total_bytes>0 && value.used_bytes<=value.total_bytes
  ? `${bytes(value.used_bytes)} / ${bytes(value.total_bytes)} · ${number(value.used_bytes/value.total_bytes*100,0)}%`
  : 'Not reported';

export function switchHealth(state = {}, unavailable = false) {
  const stats = state.stats || {};
  const idle = stats.switch_cpu_idle;
  const hasCpuSample = finite(stats.cpu_sample_age_ms);
  const resources=stats.switch_health || {},resourceRows=[];
  const temperatures=Array.isArray(resources.temperatures)?resources.temperatures.filter(sensor=>Number.isFinite(sensor.celsius)):[];
  if(temperatures.length) for(const sensor of temperatures) resourceRows.push([`Temperature · ${sensor.label || 'Switch'}`,`${sensor.celsius.toLocaleString('en-US',{maximumFractionDigits:1})} °C`]);
  else resourceRows.push(['Temperature','Not reported']);
  resourceRows.push(['Memory used',usage(resources.memory)]);
  const storage=Array.isArray(resources.storage)?resources.storage:[];
  if(storage.length) for(const drive of storage) resourceRows.push([`Storage · ${drive.mount}`,usage(drive)]);
  else resourceRows.push(['Storage used','Not reported']);
  resourceRows.push(['Resource sample age',finite(stats.health_sample_age_ms)?`${number(stats.health_sample_age_ms/1000)} s`:'—']);
  const poe=state.poe_summary,poeRows=[];
  if(poe){
    const fresh=!unavailable&&state.connected&&poe.status==='ready';
    poeRows.push(['PoE draw',finite(poe.draw_w)?number(poe.draw_w)+' W'+(fresh?'':' · '+(poe.status==='partial'?'Partial sample':'Last sample')):'Waiting for a sample']);
    poeRows.push(['PoE budget',finite(poe.budget_w)?number(poe.budget_w)+' W':'Not reported']);
    poeRows.push(['PoE headroom',fresh&&Number.isFinite(poe.headroom_w)?poe.headroom_w<0?number(-poe.headroom_w)+' W over budget':number(poe.headroom_w)+' W':'Waiting for a complete sample']);
    for(const consumer of (poe.consumers||[]).slice(0,5))poeRows.push(['PoE · Port '+consumer.port,number(consumer.power_w)+' W']);
  }
  return {
    status: state.reconnect_required ? 'Reconnect the switch to resume lighting.'
      : unavailable || !state.connected ? 'Last reported readings · switch disconnected'
      : stats.health_error ? 'Latest resource update unavailable · showing last readings' : 'Switch readings',
    rows: [
      ['LED commands', `${number(stats.commands_per_second)} / ${number(stats.command_budget)} per second`],
      ['Acknowledgment · 95th percentile', finite(stats.ack_p95_ms) ? `${number(stats.ack_p95_ms, 2)} ms` : '—'],
      ['CPU usage · last sample', hasCpuSample && finite(idle) && idle <= 1 ? `${number((1 - idle) * 100, 0)}%` : '—'],
      ['Load average', hasCpuSample ? number(stats.switch_load, 2) : '—'],
      ['CPU sample age', finite(stats.cpu_sample_age_ms) ? `${number(stats.cpu_sample_age_ms / 1000)} s` : '—'],
      ...resourceRows,...poeRows,
    ],
  };
}
