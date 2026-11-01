import test from 'node:test';
import assert from 'node:assert/strict';
import {CATALOG} from '../src/catalog.mjs';
import {renderEffectGallery} from '../preview/effect-gallery.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';
import {previewGrid} from '../preview/port-topology.mjs';

const effects = ['static', 'gradient', 'marquee'].map(id => CATALOG.find(effect => effect.id === id));
const stateFor = count => ({mode: 'running', num_ports: count, brightness: 100,
  zones: [{ports: Array.from({length: count}, (_, i) => i + 1), effect: {id: 'marquee', params: {}}}],
  ports: Array.from({length: count}, (_, i) => ({port: i + 1, source: {kind: 'zone'}}))});

test('Recent reorders actual cards and retains the focused effect when returning to All', context => {
  const dom = rendererDom(context, {recentEffects: ['marquee', 'gradient']});
  const cleanup = renderEffectGallery(dom.host, {effects, state: stateFor(8), selectedPorts: [1, 2]});
  dom.after(cleanup);
  const grid = dom.byClass(dom.host, 'gallery-grid')[0];
  const marquee = grid.children.find(card => card.dataset.effect === 'marquee');
  const action = dom.byClass(marquee, 'effect-card-open')[0]; action.focus();
  const filters = dom.byClass(dom.host, 'gallery-filters').find(group => group.getAttribute('aria-label') === 'Effect category');
  filters.children.find(button => button.dataset.category === 'Recent').click();
  assert.deepEqual(grid.children.filter(card => !card.hidden).map(card => card.dataset.effect), ['marquee', 'gradient']);
  assert.equal(dom.doc.activeElement, action);
  filters.children.find(button => button.dataset.category === 'All').click();
  assert.deepEqual(grid.children.map(card => card.dataset.effect), ['static', 'gradient', 'marquee']);
  assert.equal(dom.doc.activeElement, action);
});

test('each effect action describes its actual status badge', context => {
  const dom = rendererDom(context);
  const cleanup = renderEffectGallery(dom.host, {effects, state: stateFor(8), selectedPorts: [1, 2]});
  dom.after(cleanup);
  const ids = new Set();
  for (const card of dom.byClass(dom.host, 'effect-card')) {
    const action = dom.byClass(card, 'effect-card-open')[0], badge = dom.byClass(card, 'effect-card-state')[0];
    assert.equal(action.getAttribute('aria-describedby'), badge.id);
    assert.ok(!ids.has(badge.id)); ids.add(badge.id);
    if (card.dataset.effect === 'marquee') assert.equal(badge.textContent, 'Applied');
  }
});

for (const count of [16, 24, 48, 64]) {
  test(`${count} selected ports use readable preview rows without dropping ports`, context => {
    const dom = rendererDom(context), state = stateFor(count);
    const cleanup = renderEffectGallery(dom.host, {effects, state, selectedPorts: state.ports.map(port => port.port)});
    dom.after(cleanup);
    for (const preview of dom.byClass(dom.host, 'effect-preview')) {
      assert.equal(preview.children.length, count);
      assert.equal(preview.style.display, 'grid');
      const {columns,rows}=previewGrid(count);
      assert.equal(preview.style.gridTemplateColumns, `repeat(${columns}, minmax(0, 10px))`);
      assert.equal(preview.style.gap, '4px');
      assert.ok(preview.children.every(port => port.style.width === '100%'));
      const available = 235 - 2 - 44, gapSpace = (columns - 1) * 4;
      assert.ok(available >= columns*10+gapSpace, 'preview bars retain their full width at the narrowest gallery column');
      assert.ok(parseInt(preview.style.height,10)>=rows*14+(rows-1)*4+26);
    }
  });
}

test('selecting one Ethernet port still shows exactly one bar in every preview', context => {
  const dom=rendererDom(context),cleanup=renderEffectGallery(dom.host,{effects,state:stateFor(48),selectedPorts:[20]});
  dom.after(cleanup);
  for (const preview of dom.byClass(dom.host,'effect-preview')) {
    assert.equal(preview.children.length,1);
    assert.equal(preview.style.display,undefined);
  }
});

test('an optical-only switch opens the SFP gallery with no phantom copper selection', context => {
  const dom=rendererDom(context),sfp=Array.from({length:32},(_,i)=>({port:i+1,mode:'color',effect:'pulse'}));
  const cleanup=renderEffectGallery(dom.host,{effects,state:{num_ports:0,ports:[],sfp,zones:[]},selectedPorts:sfp.map(port=>port.port)});
  dom.after(cleanup);
  const cards=dom.byClass(dom.host,'effect-card');
  assert.equal(cards.length,4);
  assert.ok(cards.some(card=>card.dataset.effect==='pulse'));
  for (const preview of dom.byClass(dom.host,'effect-preview')) assert.equal(preview.children.length,32);
});
