import test from 'node:test';
import assert from 'node:assert/strict';
import {sfpNativePeriod, sfpEffectChange, sfpNativeAvailable, sfpCandidateSettings, sfpDraftPorts, sfpDurationRange} from '../preview/sfp-sync.mjs';
import {sfpPreviewColor} from '../preview/effect-gallery.mjs';

const near = (actual, expected, tolerance = 1e-8) => {
  assert.equal(actual.length, expected.length);
  actual.forEach((channel, index) => assert.ok(Math.abs(channel - expected[index]) <= tolerance,
    `channel ${index}: ${channel} differs from ${expected[index]}`));
};
const light = (changes = {}) => ({
  mode:'color', effect:'two_color', color:'#80C0FF', secondary_color:'#EF78C7',
  color_brightness:80, secondary_brightness:40, brightness:50, period:6, ...changes,
});

test('native preview requires both switch capability and the new backend playback state', () => {
  const playback = {kind:'native', periods:[2,4,6], segment_seconds:3};
  for (const state of [
    {},
    {switch:{sfp_native_gradient:true}},
    {switch:{sfp_native_gradient:true}, sfp:[{port:9}, {port:10}]},
    {sfp:[{port:9, playback}]},
    {switch:{sfp_native_gradient:false}, sfp:[{port:9, playback}]},
    {switch:{sfp_native_gradient:'true'}, sfp:[{port:9, playback}]},
  ]) assert.equal(sfpNativeAvailable(state), false);
  const state = Object.freeze({switch:{sfp_native_gradient:true}, sfp:[{port:9}, {port:10, playback}]});
  assert.equal(sfpNativeAvailable(state), true);
});

test('an older backend keeps its saved duration and cosine preview even at native-compatible periods', () => {
  const settings = Object.freeze(light({color:'#FF0000', secondary_color:'#0000FF', brightness:100, color_brightness:100, secondary_brightness:100}));
  const legacy = {switch:{sfp_native_gradient:true}, sfp:[{port:9, ...settings}]};
  const native = sfpNativeAvailable(legacy);
  near(sfpPreviewColor(settings, 'pulse', .75, 100, 2.2, native), [217.6561146012848, 0, 0]);
  near(sfpPreviewColor(settings, 'two_color', .75, 100, 2.2, native), [217.6561146012848, 0, 37.34388539871519]);
  assert.deepEqual(sfpEffectChange({...settings, period:10.5}, 'pulse', native), {effect:'pulse', period:10.5});
  assert.deepEqual(sfpEffectChange({...settings, period:4}, 'color_cycle', native), {effect:'color_cycle', period:4});
  assert.equal(settings.period, 6);
});

test('gallery candidates replacing Follow Ethernet use the same shared color and duration as Apply', () => {
  const state = {
    switch:{sfp_native_gradient:true}, sfp_sync:{mode:'ethernet', source_port:3},
    sfp:[
      {port:9, ...light({effect:'pulse', period:4}), playback:{kind:'follow'}},
      {port:10, ...light({effect:'color_cycle', color:'#FF0000', period:12}), playback:{kind:'follow'}},
    ],
  };
  const draft = {sfp:{9:{color:'#13579B'}}}, before = structuredClone({state, draft});
  const first = sfpCandidateSettings(state, 9, 'color_cycle', draft);
  const second = sfpCandidateSettings(state, 10, 'color_cycle', draft);
  assert.equal(first.color, '#13579B');
  assert.equal(second.color, '#13579B');
  assert.equal(first.period, 6);
  assert.equal(second.period, 6);
  assert.equal(second.port, 10);
  for (const time of [0, .5, 1, 3]) {
    near(sfpPreviewColor(first, 'color_cycle', time), sfpPreviewColor(second, 'color_cycle', time));
  }
  assert.deepEqual({state, draft}, before);
});

test('applied SFP output remains authoritative until a relevant light or sync draft exists', () => {
  for (const mode of ['independent', 'together', 'ethernet']) {
    const state = {sfp:[{port:9},{port:10}],sfp_sync:{mode, source_port:3}};
    assert.deepEqual(sfpDraftPorts(state), []);
    assert.deepEqual(sfpDraftPorts(state, {brightness:30}), []);
  }
  const together = {sfp:[{port:9},{port:10}],sfp_sync:{mode:'together', source_port:3}};
  assert.deepEqual(sfpDraftPorts(together, {sfp:{9:{color:'#123456'}}}), [9,10]);
  assert.deepEqual(sfpDraftPorts(together, {sfp:{10:{color:'#123456'}}}), []);
  assert.deepEqual(sfpDraftPorts({...together,sfp_sync:{mode:'independent'}}, {sfp:{10:{color:'#123456'}}}), [10]);
  assert.deepEqual(sfpDraftPorts(together, {sfp_sync:{mode:'independent'}}), [9,10]);
});

test('Follow Ethernet previews propagate only edits to the selected source port', () => {
  const state = {sfp:[{port:9},{port:10}],sfp_sync:{mode:'ethernet', source_port:3}};
  for (const draft of [
    {colors:{3:{color:'#123456'}}},
    {effect:{id:'pulse', ports:[1,3]}},
    {returnToEffect:[3]},
    {sfp_sync:{source_port:4}},
  ]) assert.deepEqual(sfpDraftPorts(state, draft), [9,10]);
  for (const draft of [
    {colors:{2:{color:'#123456'}}},
    {effect:{id:'pulse', ports:[1,2]}},
    {returnToEffect:[4]},
    {sfp:{9:{color:'#123456'}}},
  ]) assert.deepEqual(sfpDraftPorts(state, draft), []);
});

test('native duration choices reflect full effect cycles rather than individual fades', () => {
  for (const effect of ['pulse', 'two_color']) {
    for (const period of [2, 4, 6]) assert.equal(sfpNativePeriod(effect, period), true);
    for (const period of [3, 5, 8, 12]) assert.equal(sfpNativePeriod(effect, period), false);
  }
  for (const period of [6, 12, 18]) assert.equal(sfpNativePeriod('color_cycle', period), true);
  for (const period of [2, 4, 8, 30]) assert.equal(sfpNativePeriod('color_cycle', period), false);
  assert.equal(sfpNativePeriod('steady', 6), false);
});

test('native duration sliders reach only verified complete fade cycles', () => {
  for (const effect of ['pulse','two_color','color_cycle']) {
    const range=sfpDurationRange(effect,6,true);
    for (let value=range.min;value<=range.max;value+=range.step) assert.equal(sfpNativePeriod(effect,value),true);
    assert.equal(range.savedCustom,false);
  }
  assert.deepEqual(sfpDurationRange('pulse',4,true),{min:2,max:6,step:2,value:4,savedCustom:false});
  assert.deepEqual(sfpDurationRange('color_cycle',12,true),{min:6,max:18,step:6,value:12,savedCustom:false});
});

test('custom saved periods remain untouched until the duration slider is moved', () => {
  const settings=Object.freeze(light({period:10.5}));
  assert.deepEqual(sfpDurationRange(settings.effect,settings.period,true),{min:2,max:6,step:2,value:6,savedCustom:true});
  assert.equal(settings.period,10.5);
  assert.deepEqual(sfpDurationRange('pulse',4.1,false),{min:2,max:30,step:.1,value:4.1,savedCustom:false});
});

test('previewing a saved custom duration does not round or replace it', () => {
  const settings = Object.freeze(light({period:10.5}));
  sfpPreviewColor(settings, settings.effect, 1.5, 80);
  assert.equal(settings.period, 10.5);
});

test('only an explicit animated-effect change replaces an unsupported saved duration', () => {
  const settings = Object.freeze(light({period:10.5}));
  assert.deepEqual(sfpEffectChange(settings, 'two_color'), {effect:'two_color', period:10.5});
  assert.deepEqual(sfpEffectChange(settings, 'pulse'), {effect:'pulse', period:6});
  assert.deepEqual(sfpEffectChange(settings, 'color_cycle'), {effect:'color_cycle', period:6});
  assert.deepEqual(sfpEffectChange(settings, 'steady'), {effect:'steady', period:10.5});
  assert.deepEqual(sfpEffectChange(light({period:4}), 'pulse'), {effect:'pulse', period:4});
  assert.deepEqual(sfpEffectChange(light({period:12}), 'color_cycle'), {effect:'color_cycle', period:12});
  assert.equal(settings.period, 10.5);
  assert.equal(settings.effect, 'two_color');
});

test('effect changes retain each independent light’s supported or custom duration', () => {
  const settings = [Object.freeze(light({period:2})), Object.freeze(light({period:4}))];
  assert.deepEqual(settings.map(item => sfpEffectChange(item, 'pulse')), [
    {effect:'pulse', period:2}, {effect:'pulse', period:4},
  ]);
  const custom = [Object.freeze(light({period:10.5})), Object.freeze(light({period:12}))];
  assert.deepEqual(custom.map(item => sfpEffectChange(item, 'color_cycle')), [
    {effect:'color_cycle', period:6}, {effect:'color_cycle', period:12},
  ]);
  assert.deepEqual(custom.map(item => sfpEffectChange(item, 'steady')), [
    {effect:'steady', period:10.5}, {effect:'steady', period:12},
  ]);
  assert.deepEqual(custom.map(item => item.period), [10.5, 12]);
});

test('native pulse reaches dark halfway through each selected period and loops to the primary color', () => {
  const settings = light({color:'#FF0000', color_brightness:100, brightness:100});
  for (const period of [2, 4, 6]) {
    settings.period = period;
    near(sfpPreviewColor(settings, 'pulse', 0, 100, 1), [255, 0, 0]);
    near(sfpPreviewColor(settings, 'pulse', period / 4, 100, 1), [127.5, 0, 0]);
    near(sfpPreviewColor(settings, 'pulse', period / 2, 100, 1), [0, 0, 0]);
    near(sfpPreviewColor(settings, 'pulse', period * .75, 100, 1), [127.5, 0, 0]);
    near(sfpPreviewColor(settings, 'pulse', period, 100, 1), [255, 0, 0]);
  }
});

test('native two-color preview composes both color levels, saved SFP level and master once', () => {
  const settings = light();
  near(sfpPreviewColor(settings, 'two_color', 0, 80, 1), [40.96, 61.44, 81.6], .002);
  near(sfpPreviewColor(settings, 'two_color', 3, 80, 1), [38.24, 19.2, 31.84], .002);
  near(sfpPreviewColor(settings, 'two_color', 1.5, 80, 1), [39.6, 40.32, 56.72], .002);
  for (const effect of ['steady', 'pulse', 'two_color', 'color_cycle']) {
    near(sfpPreviewColor(settings, effect, 1, 0), [0, 0, 0]);
    near(sfpPreviewColor({...settings, brightness:0}, effect, 1, 100), [0, 0, 0]);
  }
});

test('native previews match backend RGB16 anchor interpolation at the configured gamma', () => {
  // SfpLight.native_anchors and engine.to_output(..., precise=True), gamma 2.2.
  const vectors = [
    ['pulse', 6, [
      [0, [40.95396471852974,61.440823948781336,81.59699142318527]],
      [.75, [35.93400442844279,53.90967284942135,71.59518428313993]],
      [1.5, [29.885748377737002,44.83583011496126,59.54459283279925]],
      [3, [0,0,0]],
      [5.25, [35.93400442844279,53.90967284942135,71.59518428313993]],
    ]],
    ['two_color', 6, [
      [0, [40.95396471852974,61.440823948781336,81.59699142318527]],
      [.75, [40.29414029413783,54.53843571603115,72.94752814170903]],
      [1.5, [39.621088113580626,46.38348692461317,62.84677109360379]],
      [3, [38.232101056960325,19.21483246681641,31.833003965396433]],
      [5.25, [40.29414029413783,54.53843571603115,72.94752814170903]],
    ]],
    ['color_cycle', 12, [
      [0, [20.39620887648304,51.23764426858132,81.59699142318527]],
      [1.5, [45.435055590391805,31.74660668190112,81.59699142318527]],
      [3, [68.2916729371186,20.39620887648304,68.45968411502898]],
      [6, [81.59699142318527,50.75991919690523,20.39620887648304]],
      [10.5, [20.39620887648304,75.37240279815471,60.29414295775652]],
    ]],
  ];
  for (const [effect, period, samples] of vectors) {
    const settings = Object.freeze(light({effect, period}));
    for (const [time, expected] of samples) {
      near(sfpPreviewColor(settings, effect, time, 80, 2.2), expected);
      near(sfpPreviewColor(settings, effect, time, 80), expected);
    }
  }
  assert.ok(sfpPreviewColor(light(), 'pulse', 1.5, 80, 2.2)[2] >
    sfpPreviewColor(light(), 'pulse', 1.5, 80, 1)[2] + 10);
});

test('color-specific brightness remains independent when one side of a native fade is off', () => {
  const settings = light({color_brightness:0});
  near(sfpPreviewColor(settings, 'two_color', 0, 80, 1), [0, 0, 0]);
  near(sfpPreviewColor(settings, 'two_color', 3, 80, 1), [38.24, 19.2, 31.84], .002);
  near(sfpPreviewColor({...settings, secondary_brightness:0}, 'two_color', 1.5, 80), [0, 0, 0]);
});

test('native cycle visits six color anchors in order and returns to the selected hue', () => {
  const settings = light({color:'#FF0000', color_brightness:100, brightness:100, period:12});
  const anchors = [[255,0,0], [255,255,0], [0,255,0], [0,255,255], [0,0,255], [255,0,255], [255,0,0]];
  anchors.forEach((anchor, index) => near(sfpPreviewColor(settings, 'color_cycle', index * 2, 100, 1), anchor));
  near(sfpPreviewColor(settings, 'color_cycle', 1, 100, 1), [255,127.5,0]);
  near(sfpPreviewColor(settings, 'color_cycle', 11, 100, 1), [255,0,127.5]);
});

test('native cycle is continuous at every segment boundary and full-cycle wrap', () => {
  const settings = light({period:12}), epsilon = 1e-7;
  for (let boundary = 0; boundary <= 12; boundary += 2) {
    const center = sfpPreviewColor(settings, 'color_cycle', boundary, 80, 2.2);
    const before = sfpPreviewColor(settings, 'color_cycle', boundary === 0 ? 12 - epsilon : boundary - epsilon, 80, 2.2);
    const after = sfpPreviewColor(settings, 'color_cycle', boundary + epsilon, 80, 2.2);
    near(before, center, .001);
    near(after, center, .001);
  }
});

test('custom durations preserve the existing cosine fade without native quantization', () => {
  const settings = light({color:'#FF0000', secondary_color:'#0000FF', color_brightness:100, secondary_brightness:100, brightness:100, period:10.5});
  const time = settings.period / 8;
  near(sfpPreviewColor(settings, 'pulse', time, 100, 2.2), [217.6561146012848, 0, 0]);
  near(sfpPreviewColor(settings, 'two_color', time, 100, 2.2), [217.6561146012848, 0, 37.34388539871519]);
  near(sfpPreviewColor(settings, 'two_color', time, 100, 1), sfpPreviewColor(settings, 'two_color', time, 100, 2.2));
});

test('steady output preserves full-precision brightness and does not animate', () => {
  const settings = light({period:10.5});
  for (const time of [0, .1, 3, 10.5, 300]) {
    near(sfpPreviewColor(settings, 'steady', time, 80, 2.2), [40.96, 61.44, 81.6]);
  }
});
