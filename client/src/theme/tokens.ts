import type { ThemeOptions } from '@mui/material/styles'

/**
 * Mode-agnostic design tokens: typography ramp, shape, and a soft layered
 * shadow scale. Fonts are loaded via <link> in index.html:
 *   - Space Grotesk   → display / headings (technical, geometric character)
 *   - Plus Jakarta Sans → body / UI
 *   - JetBrains Mono   → code / monospace
 */

export const DISPLAY_FONT = `'Space Grotesk', 'Segoe UI', system-ui, sans-serif`
export const BODY_FONT = `'Plus Jakarta Sans', 'Segoe UI', system-ui, sans-serif`
export const MONO_FONT = `'JetBrains Mono', ui-monospace, 'Cascadia Mono', Consolas, monospace`

const heading = (fontSize: string, letterSpacing: string) => ({
  fontFamily: DISPLAY_FONT,
  fontWeight: 600,
  letterSpacing,
  fontSize,
})

// A softer, more layered shadow ramp than MUI's default. Exactly 25 entries.
const softShadow = (y: number, blur: number, a1: number, a2: number) =>
  `0px ${y}px ${blur}px -${Math.round(blur / 3)}px rgba(15,23,42,${a1}), ` +
  `0px ${Math.round(y / 2)}px ${Math.round(blur / 2)}px -${Math.round(blur / 4)}px rgba(15,23,42,${a2})`

const shadows = [
  'none',
  ...Array.from({ length: 24 }, (_, i) => {
    const step = i + 1
    return softShadow(Math.max(1, Math.round(step * 1.4)), Math.max(2, step * 3), 0.06, 0.04)
  }),
] as ThemeOptions['shadows']

export const tokens: ThemeOptions = {
  shape: { borderRadius: 12 },
  shadows,
  typography: {
    fontFamily: BODY_FONT,
    h1: heading('2.75rem', '-0.02em'),
    h2: heading('2.125rem', '-0.018em'),
    h3: heading('1.75rem', '-0.015em'),
    h4: heading('1.4rem', '-0.012em'),
    h5: heading('1.2rem', '-0.01em'),
    h6: heading('1.025rem', '-0.005em'),
    subtitle1: { fontWeight: 600 },
    subtitle2: { fontWeight: 600, letterSpacing: '0.01em' },
    button: { fontWeight: 600, letterSpacing: '0.01em' },
    overline: { fontFamily: MONO_FONT, letterSpacing: '0.12em', fontWeight: 600 },
  },
}
