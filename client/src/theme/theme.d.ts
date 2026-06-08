import '@mui/material/styles'

/**
 * Add the custom `lighter` shade to every palette colour so
 * `sx={{ bgcolor: 'primary.lighter' }}` type-checks. The values are supplied
 * in palette.ts.
 */
declare module '@mui/material/styles' {
  interface PaletteColor {
    lighter: string
  }
  interface SimplePaletteColorOptions {
    lighter?: string
  }
}
