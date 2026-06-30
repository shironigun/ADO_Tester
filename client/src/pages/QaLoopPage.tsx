import SessionWorkspace from '../session/SessionWorkspace'
import { qaLoopConfig } from '../session/configs/qaLoop'

export default function QaLoopPage() {
  return <SessionWorkspace config={qaLoopConfig} />
}
