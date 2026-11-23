import SessionWorkspace from '../session/SessionWorkspace'
import { findBugsConfig } from '../session/configs/findBugs'

export default function FindBugsPage() {
  return <SessionWorkspace config={findBugsConfig} />
}
