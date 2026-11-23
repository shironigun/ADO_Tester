import SessionWorkspace from '../session/SessionWorkspace'
import { authorTestsConfig } from '../session/configs/authorTests'

export default function AuthorTestsPage() {
  return <SessionWorkspace config={authorTestsConfig} />
}
