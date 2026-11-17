# HAWASLAK NEWS | هوصلك نيوز

ASP.NET Core 8 + React/Vite + PostgreSQL news aggregation and bilingual editorial dashboard. Mobile .NET MAUI scaffold exists, but APK, EXE and iOS binaries have NOT been built.

## Current workflow
- RSS feeds collect news published in the previous hour; every 15 minutes while the Render service is awake.
- Source metadata is stored first in PostgreSQL. Duplicate source URLs are rejected.
- All configured initial feeds default to **manual review**; public readers only see published articles.
- Editorial dashboard supports Arabic headline and summary editing, category/country, publishing, unpublishing, deletion and source enable/disable.
- Multi-AI draft feature uses **Cloudflare Workers AI > GroqCloud > Cerebras > OpenRouter** in fallback order. It outputs Arabic headline, summary, category and country.
- AI only sees the original headline and source metadata, NOT an article's copyrighted full text. Without licensed excerpts, summaries must not assert unprovided details.
- No external images are automatically copied or published. Image licensing and compression/upload are a separate feature.

## AI provider setup (Render environment variables)
No API keys belong in this public repository. Create provider accounts yourself (GitHub social sign-in when offered), accept their terms and place secrets directly in the **HAWASLAK-NEWS Render web service** environment, not in GitHub. Connect any subset.

| Provider | Environment keys | Optional model |
|---|---|---|
| Cloudflare Workers AI | `CLOUDFLARE_ACCOUNT_ID`, `CLOUDFLARE_API_TOKEN` | `CLOUDFLARE_MODEL=@cf/meta/llama-3.1-8b-instruct` |
| GroqCloud | `GROQ_API_KEY` | `GROQ_MODEL=llama-3.3-70b-versatile` |
| Cerebras | `CEREBRAS_API_KEY` | `CEREBRAS_MODEL=gpt-oss-120b` |
| OpenRouter | `OPENROUTER_API_KEY` | `OPENROUTER_MODEL=openrouter/free` |

- `AI_AUTO_PREPARE=true`: every 15 minutes, process up to 5 pending articles if a provider is configured.
- `AI_DAILY_REQUEST_LIMIT=60`: maximum number of preparation attempts per UTC day per **running service instance**; this is a *soft local safeguard*, not a guaranteed provider billing cap. Set provider-side limits and stay on free plans.
- AI drafts remain pending and require manual approval in the dashboard.
- AI runs only on the backend. Dashboard displays provider configured/not configured, **never** credentials.
- If all providers fail, the article remains pending and AI batching cools down for 30 minutes.
- The dashboard also has a button to process up to 10 items and per-item AI preparation.
- Follow each vendor's free-tier, commercial-use, rate-limit, and data-processing terms; free allocations may change.
- **Do not upload confidential/private articles to third-party AI models.**

## Local development
Set `DATABASE_URL` to your independent PostgreSQL database and `ADMIN_PASSWORD` to a secret in your environment. Then:
```bash
dotnet run --project src/Hawaslak.Api
cd src/Hawaslak.Web && npm install && npm run dev
```
On Render the Dockerfile builds React and .NET and serves them together at the same HTTPS origin.

## Editorial responsibility
Keep RSS source licenses recorded; do not copy publisher articles or photographs without rights. Arabic AI summaries must be fact-checked against the original link before publication. Avoid making up quotes, figures, or factual context. Cover country of source and country of news event independently where possible.

## Deployment
Repository: https://github.com/Amr-2010/HAWASLAK-NEWS
Render service: https://dashboard.render.com/web/srv-dbuchju7bikc739i9njg
Public URL: https://hawaslak-news.onrender.com
Never connect this platform to Xerox or SakanGo databases.
