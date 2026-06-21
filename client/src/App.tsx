import { BrowserRouter } from 'react-router-dom'
import AppRoutes from './routes/AppRoutes'

// App shell: just the router. The route table lives in routes/AppRoutes (F4).
export default function App() {
  return (
    <BrowserRouter>
      <AppRoutes />
    </BrowserRouter>
  )
}
