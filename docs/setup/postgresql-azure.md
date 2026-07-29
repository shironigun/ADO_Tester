# Deploying ADO_Tester on Azure (PostgreSQL + App Service)

This guide deploys the backend to **Azure App Service** with **Azure Database for PostgreSQL Flexible
Server**. It assumes you've already verified PostgreSQL locally
([`postgresql-docker.md`](./postgresql-docker.md)). Because the app is provider-agnostic, going to Azure
is a **connection-string change — no code change**.

> **Cost:** Flexible Server is free for 12 months on a *new Azure free account* (Burstable **B1ms**, 750
> hrs/mo, 32 GB). On a paid/company subscription, the Burstable **B1ms** tier is the cheapest and is
> plenty for this workload; you can **Stop** the server when idle to save compute cost.

---

## Prerequisites
- An Azure subscription (company-provided is fine).
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az`) or the Azure Portal.
- The backend builds locally and works on Postgres in Docker.

Set some shell variables (adjust names — server names must be globally unique):
```bash
RG=adotester-rg
LOC=eastus
PG=adotester-pg-$RANDOM
PGADMIN=qaadmin
PGPASS='<a-strong-password>'
APP=adotester-api-$RANDOM
PLAN=adotester-plan
```

---

## Step 1 — Create the PostgreSQL Flexible Server
```bash
az group create -n $RG -l $LOC

az postgres flexible-server create \
  --resource-group $RG \
  --name $PG \
  --location $LOC \
  --tier Burstable --sku-name Standard_B1ms \
  --storage-size 32 \
  --version 16 \
  --admin-user $PGADMIN \
  --admin-password "$PGPASS" \
  --database-name qa \
  --public-access 0.0.0.0   # creates a firewall rule; tighten in Step 2
```
(Portal equivalent: **Create a resource → Azure Database for PostgreSQL → Flexible server**, Burstable
B1ms, 32 GB, Postgres 16, set admin user/password, create database `qa`.)

---

## Step 2 — Networking / firewall
Simplest path — allow Azure services (so App Service can reach it) and your own IP for admin:
```bash
# Allow other Azure services (App Service) to connect
az postgres flexible-server firewall-rule create \
  --resource-group $RG --name $PG \
  --rule-name allow-azure --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0

# Allow your current machine (for psql / running migrations from your laptop)
az postgres flexible-server firewall-rule create \
  --resource-group $RG --name $PG \
  --rule-name my-ip --start-ip-address <your.ip> --end-ip-address <your.ip>
```
For a hardened setup use **VNet integration / Private Endpoint** instead of public access (see the Azure
docs link at the bottom).

---

## Step 3 — Build the connection string
Azure enforces TLS, so add `SSL Mode=Require`:
```
Host=<PG>.postgres.database.azure.com;Port=5432;Database=qa;Username=<PGADMIN>;Password=<PGPASS>;SSL Mode=Require;Trust Server Certificate=true
```
- Flexible Server uses the plain admin username (no `@server` suffix).
- Test it from your machine first:
  ```bash
  psql "host=$PG.postgres.database.azure.com port=5432 dbname=qa user=$PGADMIN sslmode=require"
  ```

---

## Step 4 — Create the App Service + set app settings
```bash
az appservice plan create -g $RG -n $PLAN --sku B1 --is-linux
az webapp create -g $RG -p $PLAN -n $APP --runtime "DOTNETCORE:8.0"

# Provider + connection string (App Service app settings become env vars)
az webapp config appsettings set -g $RG -n $APP --settings \
  DATABASE_PROVIDER=postgres \
  POSTGRES_CONNECTION_STRING="Host=$PG.postgres.database.azure.com;Port=5432;Database=qa;Username=$PGADMIN;Password=$PGPASS;SSL Mode=Require;Trust Server Certificate=true"
```
Also set the other secrets the app needs (`ADOACCESS_PAT`, `ANTHROPIC_API_KEY`, etc.) as app settings —
the same keys you have in `.env`. (Alternatively, use **Service Connector** to wire the Postgres
connection automatically.)

---

## Step 5 — Deploy the backend
Any standard .NET deploy works. Simplest:
```bash
cd backend
dotnet publish src/QaAutomation.Api -c Release -o ./publish
cd publish && zip -r ../app.zip . && cd ..
az webapp deploy -g $RG -n $APP --src-path app.zip --type zip
```
(Or use the VS Code Azure App Service extension / GitHub Actions.)

---

## Step 6 — Migrate + verify
- On startup, `DatabaseInitializer` applies the Npgsql migrations automatically and creates the schema +
  the `tsvector`/GIN full-text tables. Watch the log stream:
  ```bash
  az webapp log tail -g $RG -n $APP
  ```
- If you prefer to migrate before the app boots, run from your machine (with the Azure connection string
  in the env) `dotnet ef database update --project src/QaAutomation.Infrastructure --startup-project src/QaAutomation.Api`.
- **Smoke test:** open the App Service URL, upload a KB doc, run an Author Tests flow, and confirm KB
  retrieval + dedup work (Postgres `ts_rank`).

---

## Cost & rollback notes
- **Save money:** `az postgres flexible-server stop -g $RG -n $PG` when the QA team isn't using it;
  `... start` to resume. Compute billing stops while stopped.
- **Free window:** the 12-month free B1ms allowance applies only to a *new free account*; a paid
  subscription bills the Burstable tier (cheapest).
- **Rollback to SQLite:** set `DATABASE_PROVIDER=sqlite` in the App Service settings (or run locally) and
  restart — the app reverts to the file DB. Provider switching never migrates or deletes data.

---

## References
- Azure Database for PostgreSQL — Flexible Server: https://learn.microsoft.com/azure/postgresql/flexible-server/
- Free account (12-month) details: https://learn.microsoft.com/azure/postgresql/flexible-server/how-to-deploy-on-azure-free-account
- App Service + PostgreSQL (VNet) tutorial: https://learn.microsoft.com/azure/postgresql/flexible-server/tutorial-webapp-server-vnet
- Service Connector (App Service ↔ PostgreSQL): https://learn.microsoft.com/azure/service-connector/how-to-integrate-postgres
