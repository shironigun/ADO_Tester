import { configureStore } from '@reduxjs/toolkit'

// Root Redux store. Cross-cutting UI slices (provider state, etc.) are introduced in F3; the
// placeholder reducer keeps the store valid until then.
export const store = configureStore({
  reducer: { _root: (state: Record<string, never> = {}) => state },
})

export type RootState = ReturnType<typeof store.getState>
export type AppDispatch = typeof store.dispatch
