import { QueryClient } from '@tanstack/react-query'

// Shared React Query client. Feature polling hooks migrate onto this in F2 (Author Tests first);
// the SSE useEventStream hook stays as-is.
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: { refetchOnWindowFocus: false, retry: 1, staleTime: 5_000 },
  },
})
