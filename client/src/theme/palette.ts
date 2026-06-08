import { alpha, type PaletteOptions } from '@mui/material/styles'

/**
 * Light + dark palettes for the QA Automation app.
 *
 * Both modes define a `lighter` shade on every semantic colour — the agent
 * pipeline step icons (AuthorTests / FindBugs) read `primary.lighter`,
 * `success.lighter`, `error.lighter` as their backgrounds. MUI's default
 * palette has no `lighter`, so without this the step backgrounds render
 * transparent. The `lighter` key is added to the palette types in theme.d.ts.
 */

const BRAND = {
  light: '#1565c0', // existing brand blue
  dark: '#5b9bff',  // lifted for legibility on dark surfaces
}

const withLighter = (main: string, mode: 'light' | 'dark') => ({
  main,
  lighter: alpha(main, mode === 'light' ? 0.12 : 0.22),
})

export const lightPalette: PaletteOptions = {
  mode: 'light',
  primary: withLighter(BRAND.light, 'light'),
  secondary: withLighter('#6d28d9', 'light'),
  success: withLighter('#2e7d32', 'light'),
  warning: withLighter('#ed6c02', 'light'),
  error: withLighter('#d32f2f', 'light'),
  info: withLighter('#0277bd', 'light'),
  background: {
    default: '#f5f6fa',
    paper: '#ffffff',
  },
  divider: alpha('#0b1530', 0.1),
  text: {
    primary: '#0f1729',
    secondary: '#516079',
  },
}

export const darkPalette: PaletteOptions = {
  mode: 'dark',
  primary: withLighter(BRAND.dark, 'dark'),
  secondary: withLighter('#a78bfa', 'dark'),
  success: withLighter('#66bb6a', 'dark'),
  warning: withLighter('#ffa726', 'dark'),
  error: withLighter('#f06a6a', 'dark'),
  info: withLighter('#4fc3f7', 'dark'),
  background: {
    default: '#0c0f17',
    paper: '#141925',
  },
  divider: alpha('#9db2d8', 0.16),
  text: {
    primary: '#eef2fb',
    secondary: '#9aa7c2',
  },
}
