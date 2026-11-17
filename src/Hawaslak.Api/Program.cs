using Npgsql;
var builder = WebApplication.CreateBuilder(args);
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? builder.Configuration.GetConnectionString("NewsDb");
builder.Services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins((Environment.GetEnvironmentVariable("WEB_ORIGINS") ?? "http://localhost:5173").Split(',', StringSplitOptions.RemoveEmptyEntries)).AllowAnyHeader().AllowAnyMethod()));
if (!string.IsNullOrWhiteSpace(dbUrl))
{
    var connectionString = dbUrl;
    if (Uri.TryCreate(dbUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "postgres" || uri.Scheme == "postgresql"))
    {
        var userInfo = uri.UserInfo.Split(':', 2);
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            SslMode = SslMode.Require
        };
        connectionString = cs.ConnectionString;
    }
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
}
var app = builder.Build();
app.UseCors("web");
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", app = "Hawaslak News" }));
app.MapGet("/api/news", async (HttpContext http, int? limit, CancellationToken ct) =>
{
    var ds = http.RequestServices.GetService<NpgsqlDataSource>();
    if (ds is null) return Results.Ok(Array.Empty<object>());
    var items = new List<object>();
    try
    {
        await using var cmd = ds.CreateCommand("SELECT id, title_ar, title_en, source_name, source_url, published_at FROM articles WHERE status = 'published' ORDER BY published_at DESC LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit ?? 30, 1, 100));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new { id = reader.GetGuid(0), titleAr = reader.GetString(1), titleEn = reader.IsDBNull(2) ? null : reader.GetString(2), source = reader.GetString(3), url = reader.GetString(4), publishedAt = reader.GetDateTime(5) });
        return Results.Ok(items);
    }
    catch (PostgresException ex) when (ex.SqlState == "42P01")
    {
        return Results.Ok(items);
    }
});
app.MapFallbackToFile("index.html");
app.Run();