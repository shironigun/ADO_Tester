# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — build & publish the .NET 8 API
# Restore is done against the copied .csproj files first so the (slow) NuGet
# restore layer is cached and only re-runs when a project file changes.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY QaAutomation.sln ./
COPY src/QaAutomation.Core/QaAutomation.Core.csproj                     src/QaAutomation.Core/
COPY src/QaAutomation.Infrastructure/QaAutomation.Infrastructure.csproj src/QaAutomation.Infrastructure/
COPY src/QaAutomation.Api/QaAutomation.Api.csproj                       src/QaAutomation.Api/
COPY src/QaAutomation.Migrations.Npgsql/QaAutomation.Migrations.Npgsql.csproj src/QaAutomation.Migrations.Npgsql/
RUN dotnet restore src/QaAutomation.Api/QaAutomation.Api.csproj

COPY . .
RUN dotnet publish src/QaAutomation.Api/QaAutomation.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — runtime: ASP.NET Core + Node.js + the Claude CLI
# The default LLM provider (ClaudeCli) shells out to the `claude` binary and
# authenticates with a Claude Pro/Max SUBSCRIPTION login — no API key. Node.js
# is required because the CLI ships as an npm package. See docs/setup/docker-deploy.md
# for the one-time `docker compose exec backend claude` login step.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl → container healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Node.js 20 pulled from the official Node image. aspnet:8.0 and node:20 share the
# Debian "bookworm" base, so the copied runtime is ABI-compatible. Then install the
# Claude CLI and GitHub Copilot CLI globally (land on PATH under /usr/local/bin).
COPY --from=node:20-bookworm-slim /usr/local/bin/node             /usr/local/bin/node
COPY --from=node:20-bookworm-slim /usr/local/lib/node_modules     /usr/local/lib/node_modules
RUN ln -sf /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -sf /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
    && npm install -g @anthropic-ai/claude-code \
    && npm install -g @github/copilot --force \
    && npm cache clean --force

# HOME must be stable + writable so the Claude CLI can persist its subscription
# credentials, AND it must be the SAME for the running app and the `docker exec … claude`
# login shell. Mount a volume at $HOME (see docker-compose.yml) so the login survives
# container recreation. Storage__DataDirectory points KB uploads / reports / SQLite at
# the mounted data volume (see Program.cs).
ENV HOME=/home/appuser \
    CLAUDE_CLI_PATH=claude \
    COPILOT_CLI_PATH=copilot \
    Storage__DataDirectory=/app/data \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
RUN mkdir -p /home/appuser /app/data

WORKDIR /app
COPY --from=build /app/publish ./

EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
    CMD curl -fsS http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "QaAutomation.Api.dll"]
