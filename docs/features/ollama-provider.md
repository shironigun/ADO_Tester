# Feature: Native Ollama Provider

**Status:** Complete (verified 2026-07-30)  
**Tests:** 187 backend tests pass; native cloud chat returned `OK`

---

## What it is

ADO_Tester now offers **Ollama** as a separate LLM provider. It uses Ollama’s native `/api/chat`
endpoint for both a local Ollama daemon and Ollama Cloud.

The existing `OpenAICompatible` provider remains unchanged for GitHub Models, OpenAI, Groq, and
other OpenAI-style gateways.

## Configuration

| Setting | Environment variable | Purpose |
| --- | --- | --- |
| `ollama.base_url` | `OLLAMA_BASE_URL` | Native Ollama API root, such as `http://localhost:11434/api` or `https://ollama.com/api` |
| `ollama.api_key` | `OLLAMA_API_KEY` | Optional locally; required by authenticated cloud endpoints |
| `ollama.model` | `OLLAMA_MODEL` | Ollama model name |

Settings-page values are persisted in the standard runtime settings overlay and take precedence over
the `.env` values.

## Data flow

```text
Settings / .env
  → OllamaOptions
  → LlmClientFactory (LlmProvider.Ollama)
  → OllamaLlmClient
  → POST {baseUrl}/chat, stream=false
  → response.message.content
```

The client normalizes a supplied host root to `/api`, so both `https://ollama.com` and
`https://ollama.com/api` are accepted.

## Key files

- `backend/src/QaAutomation.Core/Configuration/OllamaOptions.cs`
- `backend/src/QaAutomation.Infrastructure/Llm/OllamaLlmClient.cs`
- `backend/src/QaAutomation.Infrastructure/Llm/LlmClientFactory.cs`
- `backend/src/QaAutomation.Infrastructure/Settings/AppSettingsService.cs`
- `client/src/pages/SettingsPage.tsx`

## Limitations

The provider uses non-streaming completions, like the other HTTP API providers. Claude CLI remains
the streaming provider.
