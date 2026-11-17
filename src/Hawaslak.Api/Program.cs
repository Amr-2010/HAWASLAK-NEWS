using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("rss", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("HawaslakNews/0.2 (+news aggregator; contact site administrator)"); });
builder.Services.AddSingleton<NewsStore>();
builder.Services.AddSingleton<NewsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NewsCollector>());
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", app = "HAWASLAK NEWS" }));
app.MapGet("/api/news", async (NewsStore store, string? country, string? category, CancellationToken ct) => Results.Ok(await store.News(country, category, ct)));
app.MapGet("/api/admin/status", async (HttpRequest request, NewsStore store, CancellationToken ct) =>
    Authorized(request) ? Results.Ok(await store.Status(ct)) : Results.Unauthorized());
app.MapGet("/api/admin/articles", async (HttpRequest request, NewsStore store, CancellationToken ct) =>
    Authorized(request) ? Results.Ok(await store.AdminArticles(ct)) : Results.Unauthorized());
app.MapPost("/api/admin/collect", async (HttpRequest request, NewsCollector collector, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var count = await collector.CollectOnce(ct);
    return Results.Ok(new { collected = count });
});
app.MapPost("/api/admin/sources", async (HttpRequest request, NewsStore store, SourceInput input, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (!Uri.TryCreate(input.FeedUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        return Results.BadRequest(new { error = "HTTPS RSS URL required" });
    if (string.IsNullOrWhiteSpace(input.Name) || input.CountryCode.Length != 2)
        return Results.BadRequest(new { error = "Name and ISO country code required" });
    await store.AddSource(input, ct);
    return Results.Ok(new { saved = true });
});
app.MapFallbackToFile("index.html");
app.Run();

bool Authorized(HttpRequest request)
{
    var secret = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
    if (string.IsNullOrWhiteSpace(secret)) return false;
    var submitted = request.Headers["X-Admin-Password"].ToString();
    return submitted.Length == secret.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(submitted), Encoding.UTF8.GetBytes(secret));
}
record SourceInput(string Name, string CountryCode, string FeedUrl, bool AutoPublishAllowed, string LicenseNotes);
record FeedItem(string Title, string Url, string? Author, DateTimeOffset Published, string? Excerpt);

sealed class NewsStore
{
    private readonly string? _db = Environment.GetEnvironmentVariable("DATABASE_URL");
    private readonly SemaphoreSlim _setup = new(1, 1);
    private bool _ready;
    public bool Configured => !string.IsNullOrWhiteSpace(_db);
    private string ConnectionString
    {
        get
        {
            if (_db is null) throw new InvalidOperationException("DATABASE_URL missing");
            if (!Uri.TryCreate(_db, UriKind.Absolute, out var uri) || (uri.Scheme != "postgres" && uri.Scheme != "postgresql")) return _db;
            var user = uri.UserInfo.Split(':', 2);
            return new NpgsqlConnectionStringBuilder {
                Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
                Username = Uri.UnescapeDataString(user[0]),
                Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : "",
                SslMode = SslMode.Require
            }.ConnectionString;
        }
    }
    public async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        if (!_ready)
        {
            await _setup.WaitAsync(ct);
            try
            {
                if (!_ready)
                {
                    await using var cmd = new NpgsqlCommand(@"
CREATE TABLE IF NOT EXISTS news_feeds (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 name text NOT NULL, country_code char(2) NOT NULL, feed_url text NOT NULL UNIQUE,
 enabled boolean NOT NULL DEFAULT true, auto_publish_allowed boolean NOT NULL DEFAULT false,
 license_notes text NOT NULL DEFAULT '', last_checked timestamptz, last_error text);
CREATE TABLE IF NOT EXISTS news_items (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 feed_id bigint NOT NULL REFERENCES news_feeds(id),
 title text NOT NULL, subject text, original_url text NOT NULL UNIQUE,
 author_name text, original_published_at timestamptz NOT NULL,
 imported_at timestamptz NOT NULL DEFAULT now(),
 status text NOT NULL CHECK(status IN ('pending','published','rejected')),
 image_count integer NOT NULL DEFAULT 0 CHECK(image_count BETWEEN 0 AND 10),
 published_at timestamptz);
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS country_code char(2);
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS category text NOT NULL DEFAULT 'general';
UPDATE news_items SET country_code = f.country_code FROM news_feeds f WHERE news_items.feed_id=f.id AND news_items.country_code IS NULL;
CREATE INDEX IF NOT EXISTS news_items_country_category ON news_items(country_code,category,original_published_at DESC);
CREATE INDEX IF NOT EXISTS news_items_recent ON news_items(original_published_at DESC);
CREATE TABLE IF NOT EXISTS news_images (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 item_id bigint NOT NULL REFERENCES news_items(id) ON DELETE CASCADE,
 image_url text NOT NULL, license_notes text NOT NULL, content_type text,
 byte_size integer, CHECK (byte_size IS NULL OR byte_size >= 0));
", conn);
                    await cmd.ExecuteNonQueryAsync(ct);
                    _ready = true;
                }
            }
            finally { _setup.Release(); }
        }
        return conn;
    }
    public async Task<object> News(string? country,string? category,CancellationToken ct)
    {
        if (!Configured) return Array.Empty<object>();
        await using var conn = await Open(ct);
        await using var cmd = new NpgsqlCommand(@"SELECT i.id,i.title,i.subject,f.name,i.original_url,i.author_name,i.original_published_at,i.image_count,i.country_code,i.category
FROM news_items i JOIN news_feeds f ON f.id=i.feed_id WHERE i.status='published'
AND (@country IS NULL OR i.country_code=@country)
AND (@category IS NULL OR i.category=@category)
ORDER BY i.original_published_at DESC LIMIT 100", conn);
        cmd.Parameters.Add("country", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)(string.IsNullOrWhiteSpace(country)?null:country.ToUpperInvariant())??DBNull.Value;
        cmd.Parameters.Add("category", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)(string.IsNullOrWhiteSpace(category)?null:category.ToLowerInvariant())??DBNull.Value;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await r.ReadAsync(ct)) rows.Add(new { id=r.GetInt64(0), title=r.GetString(1), subject=r.IsDBNull(2)?null:r.GetString(2), source=r.GetString(3), url=r.GetString(4), author=r.IsDBNull(5)?null:r.GetString(5), publishedAt=r.GetDateTime(6), imageCount=r.GetInt32(7), country=r.IsDBNull(8)?null:r.GetString(8).Trim(),category=r.GetString(9) });
        return rows;
    }
    public async Task<object> Status(CancellationToken ct)
    {
        if (!Configured) return new { configured=false, feeds=Array.Empty<object>(), total=0, pending=0, published=0 };
        await using var conn=await Open(ct);
        var feeds=new List<object>();
        await using (var cmd=new NpgsqlCommand("SELECT id,name,country_code,feed_url,enabled,auto_publish_allowed,license_notes,last_checked,last_error FROM news_feeds ORDER BY id",conn))
        await using (var r=await cmd.ExecuteReaderAsync(ct))
            while(await r.ReadAsync(ct)) feeds.Add(new {id=r.GetInt64(0),name=r.GetString(1),country=r.GetString(2),url=r.GetString(3),enabled=r.GetBoolean(4),autoPublish=r.GetBoolean(5),license=r.GetString(6),lastChecked=r.IsDBNull(7)?(DateTime?)null:r.GetDateTime(7),error=r.IsDBNull(8)?null:r.GetString(8)});
        await using var count=new NpgsqlCommand("SELECT count(*),count(*) FILTER (WHERE status='pending'),count(*) FILTER (WHERE status='published') FROM news_items",conn);
        await using var cr=await count.ExecuteReaderAsync(ct);
        await cr.ReadAsync(ct);
        return new { configured=true, feeds, total=cr.GetInt64(0), pending=cr.GetInt64(1), published=cr.GetInt64(2) };
    }
    public async Task<object> AdminArticles(CancellationToken ct)
    {
        if (!Configured) return Array.Empty<object>();
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("SELECT i.id,i.title,f.name,i.author_name,i.status,i.original_url,i.original_published_at,i.image_count,i.country_code,i.category FROM news_items i JOIN news_feeds f ON f.id=i.feed_id ORDER BY i.imported_at DESC LIMIT 100",conn);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        var rows=new List<object>();
        while(await r.ReadAsync(ct)) rows.Add(new {id=r.GetInt64(0),title=r.GetString(1),source=r.GetString(2),author=r.IsDBNull(3)?null:r.GetString(3),status=r.GetString(4),url=r.GetString(5),publishedAt=r.GetDateTime(6),images=r.GetInt32(7),country=r.IsDBNull(8)?null:r.GetString(8).Trim(),category=r.GetString(9)});
        return rows;
    }
    public async Task AddSource(SourceInput input,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"INSERT INTO news_feeds(name,country_code,feed_url,auto_publish_allowed,license_notes)
VALUES(@n,@c,@u,@a,@l) ON CONFLICT(feed_url) DO UPDATE SET name=EXCLUDED.name,country_code=EXCLUDED.country_code,auto_publish_allowed=EXCLUDED.auto_publish_allowed,license_notes=EXCLUDED.license_notes",conn);
        cmd.Parameters.AddWithValue("n",input.Name.Trim());
        cmd.Parameters.AddWithValue("c",input.CountryCode.ToUpperInvariant());
        cmd.Parameters.AddWithValue("u",input.FeedUrl);
        cmd.Parameters.AddWithValue("a",input.AutoPublishAllowed);
        cmd.Parameters.AddWithValue("l",input.LicenseNotes ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<List<(long Id,string Url,bool AutoPublish)>> Feeds(CancellationToken ct)
    {
        var rows=new List<(long,string,bool)>();
        if (!Configured) return rows;
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("SELECT id,feed_url,auto_publish_allowed FROM news_feeds WHERE enabled=true",conn);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) rows.Add((r.GetInt64(0),r.GetString(1),r.GetBoolean(2)));
        return rows;
    }
    public async Task<int> Import(long feedId,bool autoPublish,IEnumerable<FeedItem> items,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        var total=0;
        foreach(var item in items)
        {
            await using var cmd=new NpgsqlCommand(@"INSERT INTO news_items(feed_id,title,subject,original_url,author_name,original_published_at,status,published_at,country_code,category)
VALUES(@f,@t,@s,@u,@a,@p,@status,@published,(SELECT country_code FROM news_feeds WHERE id=@f),@category) ON CONFLICT(original_url) DO NOTHING",conn);
            cmd.Parameters.AddWithValue("f",feedId);
            cmd.Parameters.AddWithValue("t",item.Title);
            // Do not copy full article bodies. Excerpts require publisher permission and are not imported by default.
            cmd.Parameters.AddWithValue("s",DBNull.Value);
            cmd.Parameters.AddWithValue("u",item.Url);
            cmd.Parameters.AddWithValue("a",(object?)item.Author??DBNull.Value);
            cmd.Parameters.AddWithValue("p",item.Published.UtcDateTime);
            cmd.Parameters.AddWithValue("category", Classify(item.Title));
            cmd.Parameters.AddWithValue("status",autoPublish?"published":"pending");
            cmd.Parameters.AddWithValue("published",autoPublish?DateTime.UtcNow:DBNull.Value);
            total+=await cmd.ExecuteNonQueryAsync(ct);
        }
        return total;
    }
    private static string Classify(string title)
    {
        var t=title.ToLowerInvariant();
        if (new[]{"football","soccer","match","league","رياضة","مباراة","الدوري","كرة القدم"}.Any(t.Contains)) return "sports";
        if (new[]{"economy","market","stocks","business","اقتصاد","بورصة","أسعار","استثمار"}.Any(t.Contains)) return "economy";
        if (new[]{"technology","ai ","software","تكنولوجيا","تقنية","ذكاء اصطناعي"}.Any(t.Contains)) return "technology";
        if (new[]{"health","medical","hospital","صحة","طب","مستشفى"}.Any(t.Contains)) return "health";
        if (new[]{"education","school","university","تعليم","جامعة","مدرسة"}.Any(t.Contains)) return "education";
        if (new[]{"election","president","government","parliament","انتخابات","رئيس","حكومة","برلمان"}.Any(t.Contains)) return "politics";
        if (new[]{"cinema","film","music","سينما","فيلم","موسيقى","فن"}.Any(t.Contains)) return "arts";
        return "general";
    }
    public async Task MarkChecked(long id,string? error,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("UPDATE news_feeds SET last_checked=now(),last_error=@e WHERE id=@id",conn);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("e",(object?)error??DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
sealed class NewsCollector : BackgroundService
{
    private readonly NewsStore _store;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<NewsCollector> _logger;
    private readonly SemaphoreSlim _gate=new(1,1);
    public NewsCollector(NewsStore store,IHttpClientFactory clients,ILogger<NewsCollector> logger)
    { _store=store;_clients=clients;_logger=logger; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20),stoppingToken);
        while(!stoppingToken.IsCancellationRequested)
        {
            try { await CollectOnce(stoppingToken); }
            catch(Exception e) { _logger.LogError(e,"RSS cycle failed"); }
            await Task.Delay(TimeSpan.FromMinutes(15),stoppingToken);
        }
    }
    public async Task<int> CollectOnce(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0,ct)) return 0;
        try
        {
            var total=0;
            foreach(var feed in await _store.Feeds(ct))
            {
                try
                {
                    using var response=await _clients.CreateClient("rss").GetAsync(feed.Url,HttpCompletionOption.ResponseHeadersRead,ct);
                    response.EnsureSuccessStatusCode();
                    if(response.Content.Headers.ContentLength > 2_000_000) throw new InvalidOperationException("Feed too large");
                    await using var stream=await response.Content.ReadAsStreamAsync(ct);
                    var doc=await XDocument.LoadAsync(stream,LoadOptions.None,ct);
                    var now=DateTimeOffset.UtcNow;
                    var rss=doc.Descendants().Where(e=>e.Name.LocalName is "item" or "entry").Take(100);
                    var items=new List<FeedItem>();
                    foreach(var entry in rss)
                    {
                        string? Get(string key)=>entry.Elements().FirstOrDefault(e=>e.Name.LocalName==key)?.Value?.Trim();
                        var title=Get("title");var url=Get("link");
                        if(string.IsNullOrEmpty(url)) url=entry.Elements().FirstOrDefault(e=>e.Name.LocalName=="link")?.Attribute("href")?.Value;
                        var rawDate=Get("pubDate")??Get("published")??Get("updated");
                        if(!DateTimeOffset.TryParse(rawDate,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)) continue;
                        if(date<now.AddHours(-1)||date>now.AddMinutes(5)) continue;
                        if(string.IsNullOrWhiteSpace(title)||title.Length>500||!Uri.TryCreate(url,UriKind.Absolute,out var link)||link.Scheme!="https") continue;
                        var author=Get("creator")??Get("author");
                        items.Add(new FeedItem(title,link.ToString(),author,date,null));
                    }
                    total+=await _store.Import(feed.Id,feed.AutoPublish,items,ct);
                    await _store.MarkChecked(feed.Id,null,ct);
                }
                catch(Exception ex) { _logger.LogWarning(ex,"Feed {FeedId} failed",feed.Id);await _store.MarkChecked(feed.Id,ex.Message[..Math.Min(300,ex.Message.Length)],ct); }
            }
            return total;
        }
        finally { _gate.Release(); }
    }
}
