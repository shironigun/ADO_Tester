import { createSlice, type PayloadAction } from '@reduxjs/toolkit'
import type { ColorMode } from '../theme'

const STORAGE_KEY = 'qa-color-mode'

// Initial mode: persisted choice → OS preference → light. Read once at store creation.
function resolveInitialMode(): ColorMode {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'light' || saved === 'dark') return saved
  } catch { /* ignore */ }
  if (typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches) {
    return 'dark'
  }
  return 'light'
}

const colorModeSlice = createSlice({
  name: 'colorMode',
  initialState: { mode: resolveInitialMode() } as { mode: ColorMode },
  reducers: {
    toggleColorMode(state) { state.mode = state.mode === 'light' ? 'dark' : 'light' },
    setColorMode(state, action: PayloadAction<ColorMode>) { state.mode = action.payload },
  },
})

export const { toggleColorMode, setColorMode } = colorModeSlice.actions
export default colorModeSlice.reducer
