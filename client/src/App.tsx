import { BrowserRouter, Routes, Route } from 'react-router-dom'
import Layout from './layout/Layout'
import SessionDetailFullScreen from './session/SessionDetailFullScreen'
import EpicRunDetailFullScreen from './session/epicRuns'
import { authorTestsConfig } from './session/configs/authorTests'
import { findBugsConfig } from './session/configs/findBugs'

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        {/* Full-screen detail views render outside the sidebar layout. */}
        <Route path="/author-tests/epic/:batchId" element={<EpicRunDetailFullScreen config={authorTestsConfig} />} />
        <Route path="/find-bugs/epic/:batchId" element={<EpicRunDetailFullScreen config={findBugsConfig} />} />
        <Route path="/author-tests/:sessionId" element={<SessionDetailFullScreen config={authorTestsConfig} />} />
        <Route path="/find-bugs/:sessionId" element={<SessionDetailFullScreen config={findBugsConfig} />} />
        <Route path="/*" element={<Layout />} />
      </Routes>
    </BrowserRouter>
  )
}
