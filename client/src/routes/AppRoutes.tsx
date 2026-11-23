import { Routes, Route } from 'react-router-dom'
import Layout from '../layout/Layout'
import SessionDetailFullScreen from '../session/SessionDetailFullScreen'
import EpicRunDetailFullScreen from '../session/epicRuns'
import { authorTestsConfig } from '../session/configs/authorTests'
import { findBugsConfig } from '../session/configs/findBugs'
import { qaLoopConfig } from '../session/configs/qaLoop'
import { paths } from './paths'
import ScriptsBoardFullscreenPage from '../pages/scriptsBoard/ScriptsBoardFullscreenPage'

// Central route table (F4). Top-level routes live here: the full-screen detail/roll-up views render
// outside the sidebar layout; everything else falls through to Layout, which owns the in-shell page
// routes. Paths come from the session-module configs (config.route) so they stay in sync.
export default function AppRoutes() {
  return (
    <Routes>
      {/* Full-screen views render outside the sidebar layout. */}
      <Route path={`${qaLoopConfig.route}/:sessionId`} element={<SessionDetailFullScreen config={qaLoopConfig} />} />
      <Route path={`${authorTestsConfig.route}/epic/:batchId`} element={<EpicRunDetailFullScreen config={authorTestsConfig} />} />
      <Route path={`${findBugsConfig.route}/epic/:batchId`} element={<EpicRunDetailFullScreen config={findBugsConfig} />} />
      <Route path={`${authorTestsConfig.route}/:sessionId`} element={<SessionDetailFullScreen config={authorTestsConfig} />} />
      <Route path={`${findBugsConfig.route}/:sessionId`} element={<SessionDetailFullScreen config={findBugsConfig} />} />
      <Route path={paths.scriptsBoardFullscreen} element={<ScriptsBoardFullscreenPage />} />
      <Route path="/*" element={<Layout />} />
    </Routes>
  )
}
