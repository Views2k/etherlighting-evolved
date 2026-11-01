const STORAGE_KEY = 'ee.ui-preferences.v1';
const effectId = value => typeof value === 'string' && /^(?:sfp:)?[a-z][a-z0-9_]{0,63}$/.test(value);
const color = value => typeof value === 'string' && /^#[\da-f]{6}$/i.test(value) ? value.toUpperCase() : null;
const unique = (values, valid, limit) => [...new Set((Array.isArray(values) ? values : []).filter(valid))].slice(0, limit);

function normalize(value = {}) {
  if (!value || typeof value !== 'object') value = {};
  return {
    version: 1,
    favoriteEffects: unique(value.favoriteEffects, effectId, 64),
    recentEffects: unique(value.recentEffects, effectId, 12),
    favoriteColors: unique((Array.isArray(value.favoriteColors) ? value.favoriteColors : []).map(color), Boolean, 24),
    palettes: (Array.isArray(value.palettes) ? value.palettes : []).filter(item => item && typeof item.name === 'string')
      .map(item => ({name: item.name.trim().slice(0, 40), colors: unique((Array.isArray(item.colors) ? item.colors : []).map(color), Boolean, 12)}))
      .filter(item => item.name && item.colors.length).slice(0, 20),
  };
}

export function createPreferenceStore(storage) {
  const read = () => {
    try { return normalize(JSON.parse(storage.getItem(STORAGE_KEY) || '{}')); }
    catch { return normalize(); }
  };
  const write = change => {
    const next = normalize(change(read()));
    try { storage.setItem(STORAGE_KEY, JSON.stringify(next)); }
    catch { throw new Error('Could not save preferences on this device.'); }
    return next;
  };
  const toggle = (key, value, valid) => {
    if (!valid(value)) throw new TypeError('Invalid preference value.');
    return write(current => {
      const existing = current[key].includes(value), limit = key === 'favoriteColors' ? 24 : 64;
      if (!existing && current[key].length >= limit) throw new Error('Remove a favorite before adding another.');
      return {...current, [key]: existing ? current[key].filter(item => item !== value) : [...current[key], value]};
    })[key];
  };
  return {
    getFavoriteEffects: () => read().favoriteEffects,
    toggleFavoriteEffect: id => toggle('favoriteEffects', id, effectId),
    getRecentEffects: () => read().recentEffects,
    recordRecentEffect: id => {
      if (!effectId(id)) throw new TypeError('Invalid effect.');
      return write(current => ({...current, recentEffects: [id, ...current.recentEffects.filter(item => item !== id)]})).recentEffects;
    },
    getFavoriteColors: () => read().favoriteColors,
    toggleFavoriteColor: value => toggle('favoriteColors', color(value), Boolean),
    getPalettes: () => read().palettes,
    savePalette: (name, colors) => {
      const palette = normalize({palettes: [{name, colors}]}).palettes[0];
      if (!palette) throw new TypeError('Enter a palette name and at least one color.');
      return write(current => {
        const remaining = current.palettes.filter(item => item.name.toLocaleLowerCase() !== palette.name.toLocaleLowerCase());
        if (remaining.length >= 20) throw new Error('Remove a palette before saving another.');
        return {...current, palettes: [...remaining, palette]};
      }).palettes;
    },
    deletePalette: name => write(current => ({...current, palettes: current.palettes.filter(item => item.name !== name)})).palettes,
  };
}

const browserStorage = {
  getItem: key => globalThis.localStorage?.getItem(key) ?? null,
  setItem: (key, value) => {
    if (!globalThis.localStorage) throw new Error('Storage unavailable');
    globalThis.localStorage.setItem(key, value);
  },
};
export const {
  getFavoriteEffects, toggleFavoriteEffect, getRecentEffects, recordRecentEffect,
  getFavoriteColors, toggleFavoriteColor, getPalettes, savePalette, deletePalette,
} = createPreferenceStore(browserStorage);
