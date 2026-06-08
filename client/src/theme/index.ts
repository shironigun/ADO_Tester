import { createTheme, type Theme } from '@mui/material/styles'
import { tokens } from './tokens'
import { lightPalette, darkPalette } from './palette'
import { components } from './components'

export type ColorMode = 'light' | 'dark'

/** Build the full MUI theme for a given colour mode. */
export function createAppTheme(mode: ColorMode): Theme {
  return createTheme({
    ...tokens,
    palette: mode === 'light' ? lightPalette : darkPalette,
    components,
  })
}
