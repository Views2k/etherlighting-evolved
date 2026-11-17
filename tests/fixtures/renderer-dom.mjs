const fixturesByContext = new WeakMap();

export function rendererDom(context, preferences = {}) {
  const original = new Map(), cleanups = [];
  let fixtures = fixturesByContext.get(context);
  if (!fixtures) {
    fixtures = []; fixturesByContext.set(context, fixtures);
    context.after(() => {
      try { for (const restore of fixtures.reverse()) restore(); }
      finally { fixturesByContext.delete(context); }
    });
  }
  const install = (key, value) => { original.set(key, Object.getOwnPropertyDescriptor(globalThis, key)); Object.defineProperty(globalThis, key, {configurable: true, writable: true, value}); };
  const doc = {activeElement: null, createElement: tag => new Element(tag), addEventListener() {}, removeEventListener() {}};
  class Element {
    constructor(tag) {
      this.tagName = tag; this.ownerDocument = doc; this.children = []; this.parentNode = null;
      this.style = {setProperty(name, value) { this[name] = String(value); }};
      this.dataset = {}; this.attributes = new Map(); this.listeners = new Map();
      this.className = ''; this.hidden = false; this.scrollTop = 0; this.value = '';
      this.classList = {add: (...names) => {
        this.className = [...new Set([...this.className.split(' ').filter(Boolean), ...names])].join(' ');
      }, toggle: (name, enabled) => {
        const classes = new Set(this.className.split(' ').filter(Boolean));
        (enabled ?? !classes.has(name)) ? classes.add(name) : classes.delete(name);
        this.className = [...classes].join(' ');
      }};
    }
    get isConnected() { return doc.body.contains(this); }
    contains(element) { return this === element || this.children.some(child => child.contains(element)); }
    append(...elements) { for (const element of elements) this.insertBefore(element, null); }
    insertBefore(element, reference) {
      if (element.parentNode) {
        if (element.contains(doc.activeElement)) doc.activeElement = doc.body;
        const siblings = element.parentNode.children;
        siblings.splice(siblings.indexOf(element), 1);
      }
      this.children.splice(reference ? this.children.indexOf(reference) : this.children.length, 0, element);
      element.parentNode = this;
    }
    replaceChildren(...elements) { for (const child of this.children) child.parentNode = null; this.children = []; this.append(...elements); }
    setAttribute(key, value) { this.attributes.set(key, String(value)); }
    getAttribute(key) { return this.attributes.get(key) ?? null; }
    addEventListener(type, listener) { this.listeners.set(type, listener); }
    focus() { doc.activeElement = this; }
    click() { this.listeners.get('click')?.({target: this}); }
  }
  doc.body = new Element('body'); doc.activeElement = doc.body;
  const host = new Element('main'); doc.body.append(host);
  let request = 0;
  const media = {matches: false, addEventListener() {}, removeEventListener() {}};
  install('document', doc); install('location', {search: '', protocol: 'http:', hostname: '127.0.0.1', port: '4180'});
  install('matchMedia', () => media);
  install('requestAnimationFrame', () => ++request); install('cancelAnimationFrame', () => {});
  install('IntersectionObserver', class {observe() {} disconnect() {}});
  install('localStorage', {getItem: () => JSON.stringify(preferences), setItem() {}});
  fixtures.push(() => {
    try { for (const cleanup of cleanups.reverse()) cleanup(); }
    finally { for (const [key, descriptor] of original) descriptor ? Object.defineProperty(globalThis, key, descriptor) : delete globalThis[key]; }
  });
  const findAll = (root, predicate) => [root, ...root.children.flatMap(child => findAll(child, predicate))].filter(predicate);
  return {doc, host, findAll, after: cleanup => cleanups.push(cleanup), byClass: (root, name) => findAll(root, element => element.className.split(' ').includes(name))};
}
