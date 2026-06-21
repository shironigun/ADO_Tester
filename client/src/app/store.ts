import { configureStore } from '@reduxjs/toolkit'
import colorMode from './colorModeSlice'

// Root Redux store. Holds cross-cutting UI state (F3). Server state lives in React Query, not here.
export const store = configureStore({
  reducer: { colorMode },
})

export type RootState = ReturnType<typeof store.getState>
export type AppDispatch = typeof store.dispatch
