# HAWASLAK NEWS | هوصلك نيوز
Independent news aggregation platform starter. **Not production ready.**

## Architecture
- ASP.NET Core 8 API (`src/Hawaslak.Api`), PostgreSQL (`schema.sql`)
- React/Vite bilingual public site (`src/Hawaslak.Web`)
- .NET MAUI Android/iOS/Windows **scaffold only** (`src/Hawaslak.Mobile`)
- Render blueprint (`render.yaml`) for isolated API + static site

## Local setup
1. Create a separate PostgreSQL database; run `schema.sql` using `psql`.
2. Set `DATABASE_URL` to a valid Npgsql connection string, e.g. `Host=localhost;Port=5432;Database=hawaslak;Username=...;Password=...` (not a postgres:// URI).
3. Run `dotnet run --project src/Hawaslak.Api`.
4. Run `cd src/Hawaslak.Web && npm install && npm run dev`; set `VITE_API_URL` to the API origin.

## Editorial safety and copyright
Sources are disabled by default. No feed scraping, syndication, full-text copying, or publication is enabled in this starter. Build licensed RSS/API adapters with per-source permission checks, provenance, rate limits, deduplication and manual review. Never publish without editorial approval. The public endpoint returns only `published` records that carry review metadata. Implement authenticated editorial APIs, roles, source connector jobs, and audit policies before any production use.

## Not yet implemented
Admin login, source collectors, AI, push notifications, localization of all app screens, and distributable APK/EXE/IPA builds. MAUI needs platform workloads; iOS signing requires Apple build/signing setup.

## Render
Create a **new separate** Render PostgreSQL instance; initialize schema. Render blueprint requires manual `DATABASE_URL` and `WEB_ORIGINS`; the static site requires `VITE_API_URL` at build time. No secrets are committed.
