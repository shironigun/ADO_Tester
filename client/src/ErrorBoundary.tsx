import { Component, type ErrorInfo, type ReactNode } from 'react'
import { logger } from './logger'

interface Props {
  children: ReactNode
}

interface State {
  hasError: boolean
}

/// Catches render-time errors anywhere below it, logs them to the backend (ErrorLogs), and shows a
/// minimal fallback instead of a blank screen.
export class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false }

  static getDerivedStateFromError(): State {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    logger.error(`React render error: ${error.message}`, {
      stack: `${error.stack ?? ''}\n\nComponent stack:${info.componentStack ?? ''}`,
      source: 'react',
    })
  }

  render(): ReactNode {
    if (this.state.hasError) {
      return (
        <div style={{ padding: 24, fontFamily: 'system-ui, sans-serif' }}>
          <h2>Something went wrong.</h2>
          <p>The error has been logged. Try reloading the page.</p>
          <button onClick={() => location.reload()}>Reload</button>
        </div>
      )
    }
    return this.props.children
  }
}
