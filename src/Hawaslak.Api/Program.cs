using Npgsql;
var builder = WebApplication.CreateBuilder(args);
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? builder.Configuration.GetConnectionString("NewsDb");
if (string.IsNullOrWhiteSpace(dbUrl)) throw new InvalidOperationException("DATABASE_URL is required");
builder.Services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins((Environment.GetEnvironmentVariable("WEB_ORIGINS") ?? "http://localhost:5173").Split(',', StringSplitOptions.RemoveEmptyEntries)).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddNpgsqlDataSource(dbUrl);
var app = builder.Build();
app.UseCors("web");
app.MapGet("/health", () => Results.Ok(new { status = "ok", app = "Hawaslak News" }));
app.MapGet("/api/news", async (NpgsqlDataSource ds, int? limit, CancellationToken ct) => {
  var items = new List<object>();
  await using var cmd = ds.CreateCommand("SELECT id, title_ar, title_en, source_name, source_url, published_at FROM articles WHERE status = 'published' ORDER BY published_at DESC LIMIT @limit");
  cmd.Parameters.AddWithValue("limit", Math.Clamp(limit ?? 30, 1, 100));
  await using var reader = await cmd.ExecuteReaderAsync(ct);
  while (await reader.ReadAsync(ct)) items.Add(new { id=reader.GetGuid(0), titleAr=reader.GetString(1), titleEn=reader.IsDBNull(2)?null:reader.GetString(2), source=reader.GetString(3), url=reader.GetString(4), publishedAt=reader.GetDateTime(5) });
  return Results.Ok(items);
});
// All editorial mutations intentionally disabled until authenticated admin roles are implemented.
app.Run();
