import * as hexColorMath from './color.mjs';
import { CATALOG } from './catalog.mjs';

export function createEffectRenderer(colorMath = hexColorMath) {
  const {hsvToHex, mix, dim, clamp, rgbToHex, hexToRgb} = colorMath;
  const presentation = {
    static: ['Ambient','sun','#E8BC78','#E8BC78','Display a single color.'],
    breathing: ['Ambient','wind','#C4A2F5','#7453A6','Fade a color in and out.'],
    color_cycle: ['Ambient','blend','#EDA7B8','#B8A2F5','Cycle through the color wheel.'],
    rainbow: ['Dynamic','rainbow','#F68C9F','#8D91ED','Move a rainbow across the group.'],
    comet: ['Dynamic','comet','#B7B7FF','#6F71C4','Move a light with a fading trail.'],
    scanner: ['Dynamic','scan','#F5787B','#B24A67','Sweep a light back and forth.'],
    marquee: ['Dynamic','arrow','#F0BE75','#F0746B','Move evenly spaced lights.'],
    starry_night: ['Ambient','sparkles','#E8DBAB','#9A874D','Fade individual lights in and out.'],
    fire: ['Ambient','flame','#FFB367','#E56846','Simulate flickering flames.'],
    police: ['Dynamic','zap','#F5787B','#6A95EE','Alternate between two colors.'],
    strobe: ['Dynamic','zap','#E4E7EF','#8998AD','Flash ports at a set interval. Can affect people who are sensitive to flashing light.'],
    gradient: ['Ambient','blend','#79DCC0','#A38DE0','Blend two colors across the group.'],
    ripple: ['Dynamic','water','#8CDDEB','#5784BA','Expand waves across the group.'],
    lightning: ['Dynamic','zap','#C9D9FC','#7381A8','Flash over a background color.'],
    daylight: ['Ambient','sun','#ECC896','#B18BC0','Change color with the time of day.'],
    traffic: ['Data','activity','#7FA3EE','#EE8E8A','Follow device traffic with automatic link-speed scaling.'],
    aurora: ['Ambient','aurora','#6ED8B2','#A38DDF','Drift overlapping curtains of color.'],
    lava_lamp: ['Ambient','blend','#ED988B','#BA86D7','Let soft color shapes merge and separate.'],
    plasma: ['Dynamic','wave','#AD8FDF','#E883AA','Blend intersecting waves of color.'],
    ocean_swell: ['Ambient','water','#65CDBE','#5B90D3','Roll gentle waves across the ports.'],
    eclipse: ['Ambient','sun','#D6B88E','#71658F','Move a soft shadow through the base color.'],
    meteor_shower: ['Dynamic','comet','#A5CAF0','#9C90DA','Send overlapping streaks with fading trails.'],
    counterflow: ['Dynamic','arrow','#71CEC4','#DB91B5','Pass two color streams in opposite directions.'],
    color_dissolve: ['Ambient','blend','#CBA2D8','#7BCDB9','Fade into a new color in a shuffled order.'],
    ink_spread: ['Dynamic','water','#9A9CDC','#EDAC95','Let color spread outward from changing points.'],
    alternating_fade: ['Ambient','layers','#8EB4E3','#DCA0A6','Crossfade the odd and even ports.'],
    color_tide: ['Dynamic','water','#73CECA','#8597D9','Fill the row with color, then let it recede.'],
    palette_morph: ['Ambient','rainbow','#DDA9A2','#91BDCC','Transform a flowing multicolor gradient.'],
    ember_bed: ['Ambient','flame','#EFA76E','#B55D64','Let warm pockets of light glow and cool.'],
    fireflies: ['Ambient','sparkles','#DACB8D','#8CA985','Follow small lights as they wander and pulse.'],
    traffic_trails: ['Data','activity','#7CAFD9','#B5D49C','Animate fading trails with port activity.'],
    activity_glow: ['Data','activity','#72E0BD','#547FAA','Brighten busy ports and fade as traffic settles.'],
    poe_usage: ['Data','zap','#4DA6FF','#EF78C7','Follow the power each port supplies.']
  };
  function presentEffect(effect) {
    const custom = Object.hasOwn(presentation,effect.id) ? presentation[effect.id] : null;
    const defaults = effect.params.filter(param => param.type === 'color' && /^#[\da-f]{6}$/i.test(param.default)).map(param => param.default);
    const categories = {static:'Ambient',animated:'Dynamic',time:'Ambient',data:'Data'};
    const fallback = [Object.hasOwn(categories,effect.category) ? categories[effect.category] : 'Other',
      'blend',defaults[0] || '#98A0AC',defaults[1] || defaults[0] || '#68717D',typeof effect.description === 'string' ? effect.description : ''];
    const [category,icon,a,b,description] = custom || fallback;
    const name = typeof effect.name === 'string' ? effect.name.replace('Colour','Color') : effect.id;
    return { ...effect,name,category,icon,colors:[a,b],description,supportsPreview:Boolean(custom) };
  }
  const EFFECTS = CATALOG.map(presentEffect);
  const LIBRARY = [...EFFECTS];
  let currentCatalog = CATALOG;
  function setEffectCatalog(catalog) {
    if (catalog === currentCatalog) return;
    if (!Array.isArray(catalog) || !catalog.length || catalog.some(e => typeof e.id !== 'string' || !Array.isArray(e.params))) {
      throw new TypeError('Invalid effect catalog');
    }
    const effects = structuredClone(catalog).map(presentEffect);
    EFFECTS.splice(0,EFFECTS.length,...effects);
    LIBRARY.splice(0,LIBRARY.length,...effects);
    currentCatalog = catalog;
  }
  const effectById = id => EFFECTS.find(e => e.id === id) || EFFECTS[0];
  const synchronizedBreathing = config => config.id === 'breathing' && config.params?.playback === 'synchronized';
  const breathingPeriod = pace => ({1:3.08,2:2,3:1})[pace] ?? 2;
  function speedParam(effect,params = {}) {
    if (effect.id === 'breathing' && params.playback === 'synchronized') return effect.params.find(p => p.key === 'pace') || null;
    return ['speed','period','rate'].map(key => effect.params.find(p => p.key === key)).find(Boolean) || null;
  }
  function speedSliderValue(param,value) {
    return param.key === 'period' ? param.min + param.max - value : value;
  }
  function speedParamValue(param,value) {
    const raw = speedSliderValue(param,Number(value));
    const step = param.step || .1;
    return Number(Math.max(param.min,Math.min(param.max,param.min + Math.round((raw - param.min) / step) * step)).toFixed(6));
  }
  function speedText(param,value) {
    if (param.key === 'pace') return ({1:'Slow',2:'Medium',3:'Fast'})[value] || 'Medium';
    const number = Number(Number(value).toFixed(2));
    if (param.key === 'period') return `${number} s / cycle`;
    if (param.key === 'speed' && value === 0) return 'Still';
    return `${number}${param.unit ? ` ${param.unit}` : '×'}`;
  }
  function defaultConfig(id = 'gradient') {
    const effect = effectById(id), params = Object.fromEntries(effect.params.map(p => [p.key,p.default]));
    return {id:effect.id,params};
  }
  function effectConfigForSelection(id,state) {
    const config = defaultConfig(id);
    if (id === 'breathing' && state.switch?.native_breathing === true && 'playback' in config.params) {
      config.params.playback = 'synchronized';
      config.params.pace = state.zones?.find(zone => synchronizedBreathing(zone.effect))?.effect.params.pace ?? 2;
    }
    return config;
  }
  function shareBreathingPace(zones,config) {
    if (!synchronizedBreathing(config)) return zones;
    return zones.map(zone => synchronizedBreathing(zone.effect) ? {...zone,effect:{...zone.effect,params:{...zone.effect.params,pace:config.params.pace}}} : zone);
  }
  function paramsAfterChange(config,key,value,zones = []) {
    const params = {...config.params,[key]:value};
    if (config.id === 'breathing' && key === 'playback' && value === 'synchronized') {
      params.pace = zones.find(zone => synchronizedBreathing(zone.effect))?.effect.params.pace ?? params.pace ?? 2;
    }
    if (value === true) for (const pair of effectById(config.id).exclusive_params || []) {
      if (pair.includes(key)) for (const other of pair) if (other !== key) params[other] = false;
    }
    return params;
  }
  const mod = (x,n) => ((x % n) + n) % n;
  const ease = x => {x = clamp(x);return x * x * (3 - 2 * x);};
  const soft = (distance,width) => Math.abs(distance) >= width ? 0 : .5 + .5 * Math.cos(Math.PI * distance / width);
  const reflected = (position,count) => count <= 1 ? 0 : (count - 1) - Math.abs(mod(position,2 * (count - 1)) - (count - 1));
  const hash01 = (index,seed) => {
    let x = (Math.imul(index,374761393) + Math.imul(seed,668265263)) >>> 0;
    x = Math.imul(x ^ (x >>> 13),1274126177) >>> 0;
    return ((x ^ (x >>> 16)) >>> 0) / 4294967295;
  };
  const colorSeeds = new Map();
  function randomColor(key,phase) {
    if (!colorSeeds.has(key)) {
      let seed = 2166136261;
      for (const byte of new TextEncoder().encode(key)) seed = Math.imul(seed ^ byte,16777619) >>> 0;
      colorSeeds.set(key,seed);
    }
    const index = Math.floor(phase),fraction = ease(phase - index),seed = colorSeeds.get(key);
    const start = hash01(index,seed),end = hash01(index + 1,seed);
    return hsvToHex((start + (mod(end - start + .5,1) - .5) * fraction) * 360,.85,1);
  }
  function resolvedColorParams(config,time,phase) {
    const effect = effectById(config.id),params = {...defaultConfig(config.id).params,...config.params};
    const colorPhase = phase ?? time / (params.color_transition || 8);
    for (const param of effect.params) if (param.type === 'color') {
      if (param.random_param && effect.params.some(item => item.key === param.random_param && item.randomizes === param.key)
          && params[param.random_param] === true && !synchronizedBreathing(config)) params[param.key] = randomColor(param.key,colorPhase);
      if (param.brightness_param && effect.params.some(item => item.key === param.brightness_param && item.dims === param.key)) {
        params[param.key] = colorBrightness(params[param.key],params[param.brightness_param]);
      }
    }
    return params;
  }
  function colorBrightness(color,value = 100) {
    const brightness = Number(value);
    return Number.isFinite(brightness) && brightness < 100 ? dim(color,clamp(brightness / 100)) : color;
  }
  const noise = (x,seed) => {const i = Math.floor(x);return hash01(i,seed) + (hash01(i + 1,seed) - hash01(i,seed)) * ease(x - i);};
  const wave = phase => .5 + .5 * Math.sin(2 * Math.PI * phase);
  const ringDistance = (position,center,count) => {const distance = mod(position - center,count);return distance > count / 2 ? distance - count : distance;};
  function softPalette(colors,value) {
    const position = clamp(value) * (colors.length - 1),index = Math.min(colors.length - 2,Math.floor(position));
    return mix(colors[index],colors[index + 1],ease(position - index));
  }
  function weighted(lights) {
    const rgb = lights.map(([color,weight]) => [hexToRgb(color),weight]);
    return rgbToHex([0,1,2].map(channel => rgb.reduce((sum,[value,weight]) => sum + value[channel] * weight,0)));
  }
  function glow(background,lights) {
    const total = lights.reduce((sum,[,weight]) => sum + weight,0);
    if (total <= 0) return background;
    const strength = 1 - Math.exp(-total);
    return weighted([[background,1 - strength],...lights.map(([color,weight]) => [color,weight * strength / total])]);
  }
  const paletteAt = (stops,value) => {
    for (let i = 1;i < stops.length;i++) if (value <= stops[i][0]) return mix(stops[i - 1][1],stops[i][1],(value - stops[i - 1][0]) / (stops[i][0] - stops[i - 1][0]));
    return stops.at(-1)[1];
  };
  const firePalettes = {
    fire:[[0,[20,0,0]],[.35,[200,10,0]],[.7,[255,90,0]],[1,[255,200,60]]],
    ice:[[0,[0,0,30]],[.4,[0,40,200]],[.75,[0,160,255]],[1,[200,240,255]]],
    toxic:[[0,[0,20,0]],[.4,[0,140,10]],[.75,[90,255,0]],[1,[220,255,120]]],
    plasma:[[0,[30,0,40]],[.4,[150,0,200]],[.75,[255,0,140]],[1,[255,180,240]]]
  };
  const daylightPalettes = {
    natural:[[0,[40,18,70]],[5.5,[40,18,70]],[6.5,[255,120,40]],[8,[255,200,140]],[12,[235,245,255]],[17,[255,210,160]],[19,[255,110,60]],[20.5,[160,50,120]],[22,[40,18,70]],[24,[40,18,70]]],
    warm:[[0,[120,30,0]],[6,[120,30,0]],[8,[255,170,90]],[13,[255,220,170]],[19,[255,140,60]],[22,[120,30,0]],[24,[120,30,0]]],
    vivid:[[0,[20,0,120]],[6,[255,60,0]],[9,[255,220,0]],[13,[0,200,255]],[18,[255,0,120]],[21,[120,0,255]],[24,[20,0,120]]]
  };
  for (const palettes of [firePalettes,daylightPalettes]) for (const key of Object.keys(palettes)) palettes[key] = palettes[key].map(([at,rgb]) => [at,rgbToHex(rgb)]);

  function phaseAt(clock,rate,time) {
    if (clock.rate !== undefined && clock.rate !== rate) clock.offset = (clock.offset || 0) + time * (clock.rate - rate);
    clock.rate = rate;
    return (clock.offset || 0) + time * rate;
  }
  function previewPhase(clock,config,time) {
    if (synchronizedBreathing(config)) return phaseAt(clock,1 / breathingPeriod(config.params.pace),time);
    return phaseAt(clock,config.params.period ? 1 / config.params.period : config.params.speed ?? config.params.rate ?? 1,time);
  }
  function previewColorPhase(clock,config,time) {
    return phaseAt(clock,1 / (config.params.color_transition || 8),time);
  }
  function activityPeak(facts) {
    return facts.reduce((peak,port) => port.link === true && port.uplink !== true && Number.isFinite(port.traffic) && port.traffic >= 0 ? Math.max(peak,port.traffic) : peak,0);
  }
  function dataPreviewFacts(count,time) {
    return Array.from({length:count},(_,index) => ({link:true,uplink:false,
      traffic:1000 * (.08 + .92 * wave(time * .075 + hash01(index,281))) ** 3,
      poe:index % 4 === 0 ? 'off' : 'poe++',
      poe_power:index % 4 === 0 ? 0 : 60 * (.02 + .98 * wave(time * .045 + hash01(index,529))) ** 2}));
  }
  function effectColor(config,index,count,time,facts = {}) {
    const p = facts.resolvedParams ?? resolvedColorParams(config,time,facts.colorPhase),id = config.id;
    const primary = p.color || p.color_a,secondary = p.color_b,background = p.background || '#000000';
    const x = index / Math.max(1,count - 1),position = count > 1 ? x : .5,travel = facts.phase ?? time * (p.speed ?? p.rate ?? 1);
    switch (id) {
      case 'static': return primary;
      case 'breathing': {
        const synchronized = synchronizedBreathing(config),period = synchronized ? breathingPeriod(p.pace) : p.period,floor = synchronized ? 0 : p.floor;
        const phase = (facts.phase ?? time / period) - (!synchronized && p.ripple ? index / (2 * count) : 0);
        return dim(primary,floor + (1 - floor) * (.5 - .5 * Math.cos(2 * Math.PI * phase)));
      }
      case 'rainbow': return hsvToHex(((p.reverse ? -travel : travel) + index * p.spread / count) * 360,p.saturation,1);
      case 'color_cycle': return hsvToHex(travel * 360,p.saturation,1);
      case 'gradient': {
        let amount = x;
        if (p.speed || travel) {
          if (p.mirror) amount = .5 - .5 * Math.cos(Math.acos(1 - 2 * x) + 2 * Math.PI * travel);
          else {const phase = mod(.8 * (.5 - Math.sin(Math.asin(1 - 2 * x) / 3)) + travel,1);amount = phase < .8 ? ease(phase / .8) : 1 - ease((phase - .8) / .2);}
        }
        return mix(primary,secondary,amount);
      }
      case 'comet': {
        if (count === 1) return primary;
        let glow = 0;
        if (p.bounce) for (let step = 0;step <= 16;step++) glow = Math.max(glow,soft(index - reflected(travel - p.tail * step / 16,count),1.5) * Math.exp(-3 * step / 16));
        else {const behind = mod(travel - index,count),distance = behind > count / 2 ? behind - count : behind;glow = Math.max(soft(distance,1.5),Math.exp(-3 * behind / p.tail) * ease((count - behind) / 1.5));}
        return mix(background,primary,glow);
      }
      case 'scanner': {if (count === 1) return primary;const head = (count - 1) * (.5 - .5 * Math.cos(Math.PI * travel / (count - 1)));return mix(background,primary,soft(index - head,p.width));}
      case 'marquee': {const offset = mod(index - travel,p.spacing);return mix(background,primary,soft(Math.min(offset,p.spacing - offset),1.25));}
      case 'starry_night': {
        const life = 1.5 + p.fade,cycle = life + 1 / p.rate,offsetTime = time + hash01(index,31) * cycle,age = mod(offsetTime,cycle);
        const glow = age < 1.5 ? ease(age / 1.5) : age < life ? 1 - ease((age - 1.5) / p.fade) : 0;
        return mix(background,p.rainbow ? colorBrightness(hsvToHex(hash01(index + Math.floor(offsetTime / cycle),81) * 360,1,1),p.brightness_color) : primary,glow);
      }
      case 'fire': return paletteAt(firePalettes[p.palette],p.intensity * (.25 + .55 * noise(travel * 1.2 + index * 7.31,index + 1) + .2 * noise(travel * 3 + index * 3.7,100 + index)));
      case 'police': {
        const phase = mod(travel,1),first = phase < .5,flash = Math.sin(Math.PI * (phase % .5) / .5) ** 2;
        if (p.pattern === 'together') return dim(first ? primary : secondary,flash);
        const left = p.pattern === 'alternate' ? index % 2 === 0 : index < Math.floor(count / 2);
        return first === left ? dim(left ? primary : secondary,flash) : '#000000';
      }
      case 'strobe': {const phase = mod(travel,1),edge = Math.min(.1 * p.rate,p.duty / 2);return phase >= p.duty ? '#000000' : dim(p.random_colors ? colorBrightness(hsvToHex(hash01(Math.floor(travel),99) * 360,1,1),p.brightness_color) : primary,ease(phase / edge) * ease((p.duty - phase) / edge));}
      case 'ripple': {const center = p.center === 0 ? (count - 1) / 2 : Math.min(p.center,count) - 1,behind = mod(travel - Math.abs(index - center),p.spacing);return mix(background,primary,soft(Math.min(behind,p.spacing - behind),1.75));}
      case 'lightning': {
        const interval = 3 + 1 / p.rate,age = mod(time,interval),strike = Math.floor(time / interval),center = Math.floor(hash01(strike,49) * count),distance = Math.abs(index - center);
        const flash = Math.max(soft(age - .5,.5),.8 * soft(age - 1.5,.65),.2 * soft(age - 1.8,1.2));
        return mix(p.sky,primary,distance <= p.reach ? flash * (1 - distance / (p.reach + 1)) : 0);
      }
      case 'daylight': {const now = new Date(),hour = facts.hour ?? now.getHours() + now.getMinutes() / 60 + now.getSeconds() / 3600;return dim(paletteAt(daylightPalettes[p.palette],hour),p.night_brightness + (1 - p.night_brightness) * ease((hour - 6) / 2) * (1 - ease((hour - 20) / 2)));}
      case 'aurora': {
        const a = wave(position * .8 - travel + .12 * Math.sin(2 * Math.PI * (travel * .31 + position)));
        const b = wave(position * 1.2 + travel * .67 + .35);
        return glow(background,[[p.color_a,1.8 * a * a],[p.color_b,1.8 * p.layers * b * b]]);
      }
      case 'lava_lamp': {
        const centers = [(count - 1) * wave(travel),(count - 1) * wave(travel * .73 + .39),(count - 1) * wave(travel * .41 + .7)];
        return glow(background,centers.map((center,j) => {
          const width = p.width * (.8 + .3 * wave(travel * .53 + j * .3));
          const weight = Math.exp(-(((index - center) / width) ** 2)) * (.65 + .65 * wave(travel * .61 + j * .4));
          return [mix(p.color_a,p.color_b,j / 2),weight];
        }));
      }
      case 'plasma': {
        const value = (wave(position * p.scale + travel) + wave(position * p.scale * .6 - travel * .73) + wave(position * p.scale * .35 + travel * .41)) / 3;
        return softPalette([p.color_a,p.color_b,p.color_c],value);
      }
      case 'ocean_swell': {
        const height = .65 * wave(position * .8 - travel) + .35 * wave(position * 1.4 + travel * .63 + .2);
        const foam = .65 * ease((height - .6) / .4),level = 1 - p.depth * (1 - height);
        return weighted([[p.deep_color,(1 - height) * (1 - foam) * level],[p.crest_color,height * (1 - foam) * level],[p.foam_color,foam * level]]);
      }
      case 'eclipse': return mix(p.color,p.shadow,p.depth * (count === 1 ? wave(travel) : soft(ringDistance(index,travel * count,count),p.width)));
      case 'meteor_shower': {
        const lights = Array.from({length:p.count},(_,j) => {
          const life = travel * (1 - j * .09) + j * .37,cycle = Math.floor(life),age = mod(life,1);
          const head = -p.tail + age * (count - 1 + 2 * p.tail),distance = head - index,width = distance >= 0 ? p.tail : 1.25;
          const weight = Math.exp(-((distance / width) ** 2)) * Math.sin(Math.PI * age) ** 2 * 2;
          return [mix(p.color_a,p.color_b,hash01(cycle,83 + j)),weight];
        });
        return glow(background,lights);
      }
      case 'counterflow': return count === 1 ? mix(p.color_a,p.color_b,wave(travel)) : glow(background,[
        [p.color_a,2 * soft(ringDistance(index,travel * count,count),p.width)],
        [p.color_b,2 * soft(ringDistance(index,(.5 - travel * .83) * count,count),p.width)]]);
      case 'color_dissolve': {
        const colors = [p.color_a,p.color_b,p.color_c],local = travel + hash01(index,31) * p.stagger,cycle = Math.floor(local);
        return mix(colors[mod(cycle,3)],colors[mod(cycle + 1,3)],ease(local - cycle));
      }
      case 'ink_spread': {
        const colors = [p.color_a,p.color_b,p.color_c],cycle = Math.floor(travel),age = mod(travel,1),origin = hash01(cycle,211) * (count - 1);
        return mix(colors[mod(cycle,3)],colors[mod(cycle + 1,3)],ease((age - p.spread * Math.abs(index - origin) / Math.max(1,count - 1)) / (1 - p.spread)));
      }
      case 'alternating_fade': return mix(p.color_a,p.color_b,wave(travel + (index % 2) * .5));
      case 'color_tide': {
        const front = -p.width / 2 + (count - 1 + p.width) * wave(travel),port = p.reverse ? count - 1 - index : index;
        return mix(background,p.color,ease((front - port) / p.width + .5));
      }
      case 'palette_morph': {
        const value = .5 - .5 * Math.cos(Math.PI * position + Math.PI * travel * p.drift),morph = wave(travel);
        return weighted([[p.first_start,(1 - value) * (1 - morph)],[p.first_end,value * (1 - morph)],
          [p.second_start,(1 - value) * morph],[p.second_end,value * morph]]);
      }
      case 'ember_bed': {
        const heat = .15 + .55 * noise(index * .7 + travel,71) + .3 * wave(travel * .37 - index * .23);
        const level = 1 - p.depth * (1 - heat);
        return weighted([[p.cool_color,(1 - heat) * level],[p.hot_color,heat * level]]);
      }
      case 'fireflies': return glow(background,Array.from({length:p.count},(_,j) => {
        const center = (count - 1) * noise(travel * .23 + j * 11,131 + j);
        const weight = soft(index - center,p.width) * wave(travel * .7 + j * .31) ** 2 * 2.5;
        return [mix(p.color_a,p.color_b,wave(travel * .19 + j * .43)),weight];
      }));
      case 'traffic_trails': {
        const state = facts.animationState,target = facts.uplink === true ? 0 : clamp((facts.traffic || 0) / p.max_mbps);
        const dt = state?.last === undefined ? 0 : Math.max(0,time - state.last);
        const online = facts.link !== false;
        let load = state?.load || 0,color;
        if (state) {
          state.last = time;
          if (state.online !== undefined && online !== state.online && state.shown) state.fade = {color:state.shown,start:time};
          state.online = online;
        }
        if (!online) {load = 0;color = p.offline_color;}
        else {
          load += (target - load) * (1 - Math.exp(-dt / (target > load ? .35 : 1.4)));
          const pulse = count === 1 ? wave(travel) : soft(ringDistance(index,travel * count,count),p.width);
          const level = .3 + .2 * pulse + .5 * pulse * load;
          color = weighted([[p.idle_color,(1 - load) * level],[p.busy_color,load * level]]);
        }
        if (state) {
          state.load = load;
          if (state.fade) {
            const progress = (time - state.fade.start) / .6;
            if (progress >= 1) delete state.fade;
            else color = mix(state.fade.color,color,ease(progress));
          }
          state.shown = color;
        }
        return color;
      }
      case 'traffic': {
        const mbps = facts.uplink === true ? 0 : facts.traffic || 0,bps = mbps * 1e6;
        const capacity = Number.isFinite(facts.speed) && facts.speed > 0 ? facts.speed : 1000;
        const load = bps <= 1e3 ? 0 : Math.log10(bps / 1e3) / Math.log10(Math.max(capacity * 1e3,10));
        const state = facts.animationState || {},dt = state.last === undefined ? .25 : Math.max(0,time - state.last);
        state.last = time;
        if (facts.link !== true) {state.level = 0;return p.offline_color;}
        if (facts.uplink === true && p.custom_uplink) {state.level = 0;return p.uplink_color;}
        const smoothing = p.smoothing ** (dt * 4);
        state.level = smoothing * (state.level || 0) + (1 - smoothing) * clamp(load);
        return mix(p.idle_color,p.busy_color,state.level);
      }
      case 'activity_glow': {
        const state = facts.animationState || {},dt = state.last === undefined ? 0 : Math.max(0,time - state.last);
        const previous = state.target || 0,speed = state.speed ?? p.speed;
        let level = state.level || 0;
        level += (previous - level) * (1 - Math.exp(-dt / ((previous > level ? .25 : 1.2) / speed)));
        const rate = facts.link === true && facts.uplink !== true && Number.isFinite(facts.traffic) && facts.traffic >= 0 ? facts.traffic : 0;
        state.target = clamp(rate / Math.max(p.minimum_mbps,facts.peakTraffic ?? rate));
        state.last = time;state.speed = p.speed;
        state.level = level;
        return mix(p.idle_color,p.active_color,state.level);
      }
      case 'poe_usage': {
        const state = facts.animationState || {},dt = state.last === undefined ? 0 : Math.max(0,time - state.last);
        const alpha = -Math.expm1(-dt * (state.speed ?? p.speed) / .6);
        state.low = (state.low || 0) + ((state.targetLow || 0) - (state.low || 0)) * alpha;
        state.high = (state.high || 0) + ((state.targetHigh || 0) - (state.high || 0)) * alpha;
        const powered = ['poe','poe+','poe++','poe+++'].includes(facts.poe) && Number.isFinite(facts.poe_power) && facts.poe_power > 0;
        const level = powered ? clamp(Math.log1p(facts.poe_power) / Math.log1p(60)) : 0;
        state.targetLow = powered ? 1 - level : 0;state.targetHigh = powered ? level : 0;
        state.last = time;state.speed = p.speed;
        const presence = state.low + state.high;
        return mix(p.no_power_color,mix(p.low_color,p.high_color,presence > 0 ? state.high / presence : 0),presence);
      }
      default: return primary;
    }
  }
  return {
    EFFECTS, LIBRARY, setEffectCatalog, effectById, synchronizedBreathing, breathingPeriod,
    speedParam, speedSliderValue, speedParamValue, speedText, defaultConfig,
    effectConfigForSelection, shareBreathingPace, paramsAfterChange, randomColor,
    resolvedColorParams, previewPhase, previewColorPhase, activityPeak, dataPreviewFacts, effectColor,
  };
}

export const {
  EFFECTS, LIBRARY, setEffectCatalog, effectById, synchronizedBreathing, breathingPeriod,
  speedParam, speedSliderValue, speedParamValue, speedText, defaultConfig,
  effectConfigForSelection, shareBreathingPace, paramsAfterChange, randomColor,
  resolvedColorParams, previewPhase, previewColorPhase, activityPeak, dataPreviewFacts, effectColor,
} = createEffectRenderer();
