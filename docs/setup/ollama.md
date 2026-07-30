# Ollama Setup

ADO_Tester supports **Ollama** as a separate native LLM provider. It uses Ollama's `/api/chat`
endpoint, rather than the OpenAI-compatible provider.

## Choose an endpoint

| Deployment | Native API base URL | API key |
| --- | --- | --- |
| Local Ollama | `http://localhost:11434/api` | Optional |
| Ollama Cloud | `https://ollama.com/api` | Required |

You may also provide the host root without `/api` (for example, `https://ollama.com`); ADO_Tester
adds `/api` automatically.

## Configure `.env`

At the repository root, add:

```dotenv
OLLAMA_BASE_URL=https://ollama.com/api
OLLAMA_API_KEY=your-ollama-api-key
OLLAMA_MODEL=llama3.2
```

For a local daemon:

```dotenv
OLLAMA_BASE_URL=http://localhost:11434/api
OLLAMA_API_KEY=
OLLAMA_MODEL=llama3.2
```

`OLLAMA_API_KEY` is optional for a default local Ollama install. It should be configured for an
authenticated cloud endpoint. Never commit `.env`.

## Local Ollama prerequisite

Install Ollama and make the selected model available:

```powershell
ollama pull llama3.2
ollama serve
```

If the Ollama desktop/service installation is already running, `ollama serve` is not needed.

## Select the provider in ADO_Tester

1. Start the backend and frontend.
2. Open **Settings**.
3. In **LLM providers**, enter the Ollama native API base URL, API key (if required), and model.
4. Save the Ollama card.
5. Use the provider selector in the app bar to choose **Ollama**.
6. Open **Settings → Diagnostics** and run the LLM check.

A successful result shows:

```text
[Ollama] OK
```

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `404` or endpoint error | Use the native `/api` URL, not an OpenAI-compatible `/v1` URL. |
| `401` or `403` | Check the cloud API key and account entitlement. |
| Model not found | Run `ollama pull <model>` locally, or use a cloud model name available to your account. |
| Connection refused locally | Confirm the Ollama service is running and port `11434` is reachable. |
| ADO_Tester still uses another provider | Select **Ollama** from the app-bar provider selector after saving Settings. |

## Provider distinction

Keep **Ollama** separate from **OpenAI-compatible**:

- Use **Ollama** for native local or Ollama Cloud APIs.
- Use **OpenAI-compatible** for GitHub Models, OpenAI, Groq, OpenRouter, and other `/v1` gateways.
