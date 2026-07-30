# Feature: QA Loop Smart Module Discovery

QA Loop can now recover from an invalid or stale module path by inspecting the selected repository and
branch rather than assuming the module is a repository-root folder.

## Flow

1. Use valid explicit frontend/source paths when supplied.
2. When those paths yield no files, list the selected branch tree.
3. Score likely frontend component/API files for the module.
4. Extract common Axios/client/fetch calls deterministically.
5. Score likely backend controller/API files from the same branch.
6. Send the bounded evidence bundle to the existing analyzer and three-level generators.

The session persists `DiscoveryJson` and derived source directories for reproducibility. The scan form
also supports an optional Source Dirs override.

## UI phase

The UI Phase toggle is now persisted at scan start. For opted-in sessions, Playwright script generation
passes the selected frontend route to its MCP agent and requires an initialized `.mcp.json` framework.
The selected API provider can handle API analysis; Playwright MCP generation remains Claude CLI-based.

## Verification

- 187 backend tests pass.
- API builds cleanly.
- Frontend production build passes.

Manual nested-module verification remains the next operation: start an authenticated scan for Inventory
with its repository and `qa` branch, then confirm the fetch stream reports smart-discovery evidence.
