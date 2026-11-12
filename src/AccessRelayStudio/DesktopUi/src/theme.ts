export type Theme = 'light' | 'dark';
export const themeChanged = 'accessrelay-theme-change';
const storageKey = 'accessrelay.theme';

export function currentTheme(): Theme {
  return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
}

export function applyTheme(theme: Theme) {
  document.documentElement.dataset.theme = theme;
  window.dispatchEvent(new Event(themeChanged));
}

export function initializeTheme() {
  let theme: Theme = 'light';
  try { if (localStorage.getItem(storageKey) === 'dark') theme = 'dark'; } catch { /* Storage can be unavailable. */ }
  applyTheme(theme);
}

export function saveTheme(theme: Theme) {
  applyTheme(theme);
  try { localStorage.setItem(storageKey, theme); } catch { /* The theme still works for this session. */ }
}
