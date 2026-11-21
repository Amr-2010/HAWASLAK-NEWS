# HAWASLAK NEWS — Prisma to Supabase PostgreSQL migration

## Status
Preparation only. No database credentials have been changed and no live data has been deleted or copied.

## Safety requirements
- Create a **separate Supabase project** for HAWASLAK NEWS. Never reuse the Xerox or other application database.
- Temporarily stop ingestion and AI publishing during the final backup/cutover to prevent missing writes.
- Preserve all tables, schemas, indexes, constraints, sequences, ads, admin accounts, feeds, visitor stats, news, and editorial content.
- Never commit DATABASE_URL, credentials, dumps, or production data to GitHub.
- Use a PostgreSQL-compatible connection string in Render's DATABASE_URL, with SSL enabled; follow Supabase's documented direct or session-pooler connection mode appropriate for the runtime.
- Do not overwrite the old Prisma connection until restoration and validation succeed.

## Suggested migration procedure
1. Create a Supabase project in the Supabase dashboard and record the connection details securely.
2. Restore temporary Prisma access (if available), then export the database with pg_dump using a PostgreSQL client version compatible with the source:
   `pg_dump --format=custom --no-owner --no-acl --file=hawaslak-backup.dump "$SOURCE_DATABASE_URL"`
3. Restore into a fresh, empty Supabase database with pg_restore. Use the project-specific connection string and an appropriate PostgreSQL client version:
   `pg_restore --no-owner --no-acl --dbname="$TARGET_DATABASE_URL" --verbose hawaslak-backup.dump`
   Inspect extension/permission errors and resolve them; do not ignore restore failures.
4. Compare table counts for news_items, news_feeds, news_ads, news_visitors, news_visitor_days, editorial tables, admin tables, and other app-owned tables. Verify indexes, sequences and app login. Validate source/target counts and representative records.
5. Set Render DATABASE_URL to the Supabase connection string only after successful validation. Redeploy and verify /health, public news, ad images, admin login, news ingestion and AI publishing.
6. Keep Prisma database intact until stable operation is confirmed; maintain a rollback connection string securely outside GitHub.

## Reducing database operations
- The public API now uses in-memory output caching: news/search and ads 60s; visitor stats 60s; news counts 90s; news filters 300s.
- Visitor heartbeat interval has been increased from 60 to 180 seconds.
- Next: batch inserts and de-duplication, use backoff on database quota/connection errors, and evaluate a cache shared across multiple instances if scaled.
- RSS ingestion remains every 15 minutes and source configuration refresh every hour; retention is 24 hours for ordinary news.

## Cautions
- Supabase Free has limits and may pause inactive projects. Check current official plan limits before cutover.
- A database backup and restore must run from a trusted environment with network access to both databases.
- The website's Render free instance may sleep; this affects background schedules.
