export const DEFAULT_THEME = 'evolved';

export const THEMES = Object.freeze([
  Object.freeze({"id": "evolved", "name": "Evolved", "mode": "dark", "description": "Violet and pink on charcoal.", "swatches": ["#19191f", "#22212b", "#bf9aff"]}),
  Object.freeze({"id": "midnight", "name": "Midnight", "mode": "dark", "description": "Blue accents on deep black.", "swatches": ["#070d18", "#0c1626", "#7db9ff"]}),
  Object.freeze({"id": "graphite", "name": "Graphite", "mode": "dark", "description": "Neutral charcoal and brushed gray.", "swatches": ["#17191c", "#202327", "#c3ccd7"]}),
  Object.freeze({"id": "ocean", "name": "Ocean", "mode": "dark", "description": "Deep teal with clear aqua accents.", "swatches": ["#0b1b22", "#112831", "#67d5e8"]}),
  Object.freeze({"id": "forest", "name": "Forest", "mode": "dark", "description": "Evergreen surfaces and soft sage.", "swatches": ["#0e1c17", "#162b23", "#91d3a4"]}),
  Object.freeze({"id": "amethyst", "name": "Amethyst", "mode": "dark", "description": "Plum surfaces and lavender light.", "swatches": ["#1c1426", "#281d35", "#d1adff"]}),
  Object.freeze({"id": "ember", "name": "Ember", "mode": "dark", "description": "Warm charcoal with copper accents.", "swatches": ["#211714", "#2f221c", "#f2b078"]}),
  Object.freeze({"id": "rose", "name": "Rose", "mode": "dark", "description": "Dark berry with rose accents.", "swatches": ["#24161e", "#34212d", "#eea1c3"]}),
  Object.freeze({"id": "slate", "name": "Slate", "mode": "dark", "description": "Blue gray surfaces and steel accents.", "swatches": ["#151c25", "#202c38", "#a4bed8"]}),
  Object.freeze({"id": "silver", "name": "Silver", "mode": "light", "description": "Silver and white to match the switch.", "swatches": ["#f1f3f5", "#ffffff", "#245b94"]}),
  Object.freeze({"id": "pearl", "name": "Pearl", "mode": "light", "description": "Soft white with muted violet accents.", "swatches": ["#f5f3f8", "#ffffff", "#6c468d"]}),
  Object.freeze({"id": "sand", "name": "Sand", "mode": "light", "description": "Warm ivory and understated bronze.", "swatches": ["#f5f0e6", "#fffdf7", "#785522"]}),
]);

const themeIds = new Set(THEMES.map(theme => theme.id));

export function validateThemeId(value) {
  return typeof value === 'string' && themeIds.has(value);
}
