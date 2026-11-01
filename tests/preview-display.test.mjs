import test from 'node:test';
import assert from 'node:assert/strict';
import {portDisplayRgb, manualDisplayRgb, masterDisplayRgb} from '../preview/port-display.mjs';

const near = (actual, expected) => actual.forEach((value, index) => assert.ok(Math.abs(value - expected[index]) < 1e-9));
const levelRule = brightness => ({enabled: true, ports: [1], conditions: [], look: {style: 'solid', color: '#4da6ff', brightness}});

test('composed display channels retain fractional intensity without decoding twice', () => {
  const display = [39.27, 84.66, 130.05];
  assert.deepEqual(portDisplayRgb({display_rgb: display, preview: '#04173a'}), display);
  near(manualDisplayRgb('#4da6ff', 51, 20), display);
});

test('legacy hardware preview is inverse-gamma decoded once', () => {
  const shown = portDisplayRgb({preview: '#4080ff'}, 2.2);
  near(shown, [64, 128, 255].map(channel => 255 * (channel / 255) ** (1 / 2.2)));
  near(portDisplayRgb({output: {color: '#4080ff', level: 50}}, 2),
    [64, 128, 255].map(channel => 255 * (channel / 255 * .5) ** .5));
});

test('unmanaged or explicitly unknown display does not show a saved configured color', () => {
  assert.deepEqual(portDisplayRgb({display_rgb: null, color: '#ffffff', preview: '#ffffff'}), [0, 0, 0]);
  assert.deepEqual(portDisplayRgb({mode: 'unmanaged', color: '#ffffff'}), [0, 0, 0]);
  assert.deepEqual(portDisplayRgb({mode: 'off', preview: '#ffffff'}), [0, 0, 0]);
  assert.deepEqual(portDisplayRgb({display_rgb: [NaN, 2, 3], preview: '#ffffff'}), [0, 0, 0]);
  assert.deepEqual(portDisplayRgb({mode: 'unmanaged', rule: {id: 'alert'}, display_rgb: [20, 30, 40]}), [20, 30, 40]);
});

test('manual brightness replaces master while null inherits it', () => {
  near(manualDisplayRgb('#4da6ff', 51, 5), [39.27, 84.66, 130.05]);
  near(manualDisplayRgb('#4da6ff', null, 20), [15.4, 33.2, 51]);
  assert.deepEqual(manualDisplayRgb('#4da6ff', 0, 100), [0, 0, 0]);
});

test('master preview scales composed display but preserves explicit override and rule levels', () => {
  const port = {port: 1, display_rgb: [39.27, 84.66, 130.05], color: '#4da6ff'};
  near(masterDisplayRgb(port, 25.5, 51), [19.635, 42.33, 65.025]);
  assert.deepEqual(masterDisplayRgb({...port, override: {brightness: 51}}, 0, 51), port.display_rgb);
  assert.deepEqual(masterDisplayRgb(port, 0, 51, [levelRule(51)]), port.display_rgb);
  assert.deepEqual(masterDisplayRgb({...port, override: {brightness: null}}, 0, 51, [levelRule(51)]), port.display_rgb);
  near(masterDisplayRgb(port, 25.5, 51, [levelRule(null)]), [19.635, 42.33, 65.025]);
});

test('SFP master preview respects scoped rule ownership and explicit brightness', () => {
  const port = {port: 9, mode: 'unmanaged', display_rgb: [39.27, 84.66, 130.05], facts: {}};
  const rule = {...levelRule(null), ports: [9]};
  near(masterDisplayRgb(port, 25.5, 51, [rule]), [19.635, 42.33, 65.025]);
  assert.deepEqual(masterDisplayRgb(port, 0, 51, [{...rule, look: {...rule.look, brightness: 51}}]), port.display_rgb);
  assert.deepEqual(masterDisplayRgb({...port, display_rgb: null}, 100, 51), [0, 0, 0]);
});
