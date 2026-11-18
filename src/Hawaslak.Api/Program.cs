using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("rss", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("HawaslakNews/0.2 (+news aggregator; contact site administrator)"); });
builder.Services.AddSingleton<NewsStore>();
builder.Services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromSeconds(25));
builder.Services.AddSingleton<NewsAiService>();
builder.Services.AddSingleton<NewsAiWorker>();
builder.Services.AddHostedService(sp=>sp.GetRequiredService<NewsAiWorker>());
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
app.MapPut("/api/admin/articles/{id:long}", async (HttpRequest request, NewsStore store, long id, ArticleEdit edit, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(edit.TitleAr) || edit.TitleAr.Length > 500 || edit.Subject?.Length > 20000 ||
        edit.CountryCode?.Length != 2 || !new[] {"general","politics","economy","sports","technology","health","education","arts"}.Contains(edit.Category))
        return Results.BadRequest(new { error = "Arabic title, two-letter country and valid category required" });
    var saved = await store.EditArticle(id,edit,ct);
    return saved ? Results.Ok(new { saved=true }) : Results.NotFound();
});
app.MapPost("/api/admin/articles/{id:long}/status", async (HttpRequest request, NewsStore store, long id, StatusEdit edit, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (!new[] {"pending","published","rejected"}.Contains(edit.Status)) return Results.BadRequest();
    var saved=await store.ChangeStatus(id,edit.Status,ct);
    return saved ? Results.Ok(new { saved=true }) : Results.NotFound();
});
app.MapPost("/api/admin/articles/publish-ready", async (HttpRequest request, NewsStore store, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return Results.Ok(await store.PublishReady(ct));
});
app.MapDelete("/api/admin/articles/{id:long}", async (HttpRequest request, NewsStore store, long id, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return await store.DeleteArticle(id,ct) ? Results.Ok(new { deleted=true }) : Results.NotFound();
});
app.MapPut("/api/admin/sources/{id:long}", async (HttpRequest request, NewsStore store, long id, SourceEdit edit, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return await store.EditSource(id,edit,ct) ? Results.Ok(new { saved=true }) : Results.NotFound();
});
app.MapPost("/api/admin/ai/prepare-batch", async (HttpRequest request, NewsAiWorker worker, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return Results.Ok(await worker.ProcessBatch(10,ct));
});
app.MapGet("/api/admin/ai/providers", (HttpRequest request, NewsAiService ai) =>
    Authorized(request) ? Results.Ok(ai.Providers()) : Results.Unauthorized());
app.MapPost("/api/admin/articles/{id:long}/ai-prepare", async (HttpRequest request, NewsStore store, NewsAiService ai, long id, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var source = await store.GetForAi(id,ct);
    if (source is null) return Results.NotFound();
    try
    {
        var draft=await ai.Generate(new AiRequest(source.Value.Title,source.Value.Context,source.Value.Source,source.Value.Country),ct);
        await store.SaveAiDraft(id,draft,ct);
        return Results.Ok(new { saved=true, draft });
    }
    catch (InvalidOperationException e) { return Results.Json(new { error=e.Message },statusCode:503); }
});
app.MapGet("/api/site-settings", async (NewsStore store,CancellationToken ct)=>Results.Ok(await store.SiteSettings(ct)));
app.MapPut("/api/admin/site-settings", async (HttpRequest request,NewsStore store,SiteSettingsInput input,CancellationToken ct)=>{
 if(!Authorized(request))return Results.Unauthorized();
 if(!string.IsNullOrWhiteSpace(input.LogoUrl)&&(!Uri.TryCreate(input.LogoUrl,UriKind.Absolute,out var u)||u.Scheme!="https"||input.LogoUrl.Length>1000))return Results.BadRequest(new {error="Logo must be a valid HTTPS image URL"});
 if(input.Categories is null||input.Categories.Count>24||input.Categories.Any(x=>string.IsNullOrWhiteSpace(x.Id)||!System.Text.RegularExpressions.Regex.IsMatch(x.Id,@"^[a-z][a-z0-9_-]{1,30}$")||string.IsNullOrWhiteSpace(x.Ar)||x.Ar.Length>60||string.IsNullOrWhiteSpace(x.En)||x.En.Length>60)||input.Categories.Select(x=>x.Id).Distinct().Count()!=input.Categories.Count)return Results.BadRequest(new {error="Invalid categories"});
 await store.SaveSiteSettings(input,ct);return Results.Ok(new {saved=true});
});
app.MapGet("/api/features", async (NewsStore store, CancellationToken ct) => Results.Ok(await store.Features(false,ct)));
app.MapGet("/api/admin/features", async (HttpRequest request, NewsStore store, CancellationToken ct) =>
    Authorized(request) ? Results.Ok(await store.Features(true,ct)) : Results.Unauthorized());
app.MapPost("/api/admin/features", async (HttpRequest request, NewsStore store, FeatureInput input, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Body) || input.Title.Length>240 || input.Body.Length>40000 || !new[]{"article","special_file","news"}.Contains(input.Kind))
        return Results.BadRequest(new {error="Valid title, body and kind are required"});
    return Results.Ok(new {id=await store.SaveFeature(input,ct)});
});
app.MapDelete("/api/admin/features/{id:long}", async (HttpRequest request, NewsStore store, long id, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return await store.DeleteFeature(id,ct) ? Results.Ok(new {deleted=true}) : Results.NotFound();
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
record SiteCategory(string Id,string Ar,string En,string Icon);
record SiteSettingsInput(string? LogoUrl,List<SiteCategory> Categories);
record FeatureInput(long? Id,string Kind,string Title,string Body,string? Excerpt,string? CoverUrl,bool Published);
record SourceInput(string Name, string CountryCode, string FeedUrl, bool AutoPublishAllowed, string LicenseNotes);
record ArticleEdit(string TitleAr,string? Subject,string CountryCode,string Category);
record StatusEdit(string Status);
record SourceEdit(bool Enabled,bool AutoPublishAllowed);
record FeedItem(string Title, string Url, string? Author, DateTimeOffset Published, string? Excerpt, string? ImageUrl);

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
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes)
VALUES
 ('Al Jazeera English','QA','https://www.aljazeera.com/xml/rss/all.xml',true,false,'RSS discovery only; review rights before publication'),
 ('DW English','DE','https://rss.dw.com/rdf/rss-en-all',true,false,'RSS discovery only; review rights before publication'),
 ('The Guardian World','GB','https://www.theguardian.com/world/rss',true,false,'RSS discovery only; review rights before publication')
ON CONFLICT(feed_url) DO NOTHING;
-- Curated discovery candidates: each feed is monitored independently; publishing remains manual until rights are verified.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes)
VALUES
('Al-Ahram Egypt','EG','https://gate.ahram.org.eg/rss',true,false,'Candidate RSS feed; verify access and republication rights'),
('Youm7 Egypt','EG','https://www.youm7.com/rss',true,false,'Candidate RSS feed; verify access and republication rights'),
('Al-Masry Al-Youm','EG','https://www.almasryalyoum.com/rss/',true,false,'Candidate RSS feed; verify access and republication rights'),
('Mada Masr','EG','https://www.madamasr.com/ar/feed/',true,false,'Candidate RSS feed; verify access and republication rights'),
('Egypt Independent','EG','https://www.egyptindependent.com/feed/',true,false,'Candidate RSS feed; verify access and republication rights'),
('Daily News Egypt','EG','https://www.dailynewsegypt.com/feed/',true,false,'Candidate RSS feed; verify access and republication rights'),
('Ahram Online','EG','https://english.ahram.org.eg/rss.aspx',true,false,'Candidate RSS feed; verify access and republication rights'),
('BBC UK','GB','https://feeds.bbci.co.uk/news/uk/rss.xml',true,false,'Candidate RSS feed; verify access and republication rights'),
('BBC World','GB','https://feeds.bbci.co.uk/news/world/rss.xml',true,false,'World coverage; country identifies publisher, not event'),
('France 24 France','FR','https://www.france24.com/en/france/rss',true,false,'Candidate RSS feed; verify access and republication rights'),
('Euronews Europe','FR','https://www.euronews.com/rss',true,false,'World coverage; country identifies publisher, not event'),
('Al Jazeera Arabic','QA','https://www.aljazeera.net/aljazeera/rss/ar',true,false,'Candidate RSS feed; verify access and republication rights'),
('The Straits Times Singapore','SG','https://www.straitstimes.com/news/singapore/rss.xml',true,false,'Candidate RSS feed; verify access and republication rights'),
('CNA Singapore','SG','https://www.channelnewsasia.com/api/v1/rss-outbound-feed?_format=xml',true,false,'Candidate RSS feed; verify access and republication rights'),
('NHK Japan','JP','https://www.nhk.or.jp/rss/news/cat0.xml',true,false,'Candidate RSS feed; verify access and republication rights'),
('DW Germany','DE','https://rss.dw.com/xml/rss-en-all',true,false,'Candidate RSS feed; verify access and republication rights'),
('Sky News UK','GB','https://feeds.skynews.com/feeds/rss/home.xml',true,false,'Candidate RSS feed; verify access and republication rights')
ON CONFLICT(feed_url) DO NOTHING;
-- Pause known failing endpoints (including the duplicate Youm7 feed); keep collected items.
UPDATE news_feeds SET enabled=false,
 license_notes=concat_ws(' | ',NULLIF(license_notes,''),'Paused: invalid RSS or blocked; verify an official feed URL')
 WHERE feed_url IN ('https://www.youm7.com/rss','https://www.youm7.com/rss/SectionRss?SectionID=319','https://www.aljazeera.net/aljazeera/rss/ar','https://gate.ahram.org.eg/rss','https://english.ahram.org.eg/rss.aspx','https://www.almasryalyoum.com/rss/');
-- Repair previously seeded Egyptian feeds without deleting collected articles.
UPDATE news_feeds SET feed_url='https://www.youm7.com/rss/SectionRss?SectionID=319',last_error=NULL,last_checked=NULL,enabled=false
 WHERE name='Youm7 Egypt' AND feed_url='https://www.youm7.com/rss'
 AND NOT EXISTS(SELECT 1 FROM news_feeds WHERE feed_url='https://www.youm7.com/rss/SectionRss?SectionID=319');
UPDATE news_feeds SET enabled=false,license_notes=concat_ws(' | ',NULLIF(license_notes,''),'Paused: feed endpoint blocked or invalid; verify official RSS before re-enabling')
 WHERE name IN ('Al-Ahram Egypt','Ahram Online','Al-Masry Al-Youm')
 AND (feed_url IN ('https://gate.ahram.org.eg/rss','https://english.ahram.org.eg/rss.aspx','https://www.almasryalyoum.com/rss/'));
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
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS title_ar text;
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS rss_excerpt text;
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS source_image_url text;
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS ai_provider text;
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS ai_model text;
ALTER TABLE news_items ADD COLUMN IF NOT EXISTS ai_processed_at timestamptz;
-- Arabic-language headlines are editorially ready without translation AI; keep pending for admin approval.
UPDATE news_items SET title_ar=title
WHERE NULLIF(btrim(title_ar),'') IS NULL
  AND title ~ '[ء-ي].*[ء-ي].*[ء-ي]'
  AND length(regexp_replace(title,'[^A-Za-z]','','g')) < length(regexp_replace(title,'[^ء-ي]','','g'));
CREATE INDEX IF NOT EXISTS news_items_country_category ON news_items(country_code,category,original_published_at DESC);
CREATE INDEX IF NOT EXISTS news_items_recent ON news_items(original_published_at DESC);
-- International source expansion: publicly documented discovery feeds only.
-- This is a starter collection, NOT a verified source in every country. Never grant republication rights automatically.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('BBC World','GB','https://feeds.bbci.co.uk/news/world/rss.xml',true,false,'Discovery only; publisher rights unverified'),
('BBC UK','GB','https://feeds.bbci.co.uk/news/uk/rss.xml',true,false,'Discovery only; publisher rights unverified'),
('Sky News UK','GB','https://feeds.skynews.com/feeds/rss/home.xml',true,false,'Discovery only; publisher rights unverified'),
('The Independent UK','GB','https://www.independent.co.uk/rss',true,false,'Discovery only; publisher rights unverified'),
('Politico Europe','BE','https://www.politico.eu/feed/',true,false,'Discovery only; publisher rights unverified'),
('Le Monde English','FR','https://www.lemonde.fr/en/rss/une.xml',true,false,'Discovery only; publisher rights unverified'),
('Der Spiegel International','DE','https://www.spiegel.de/international/index.rss',true,false,'Discovery only; publisher rights unverified'),
('DW World English','DE','https://rss.dw.com/xml/rss-en-all',true,false,'Discovery only; publisher rights unverified'),
('Euronews','FR','https://www.euronews.com/rss',true,false,'Discovery only; publisher rights unverified'),
('RTE Ireland','IE','https://www.rte.ie/feeds/rss/?index=/news/',true,false,'Discovery only; publisher rights unverified'),
('Al Jazeera English Global','QA','https://www.aljazeera.com/xml/rss/all.xml',true,false,'Discovery only; publisher rights unverified'),
('The Guardian World Edition','GB','https://www.theguardian.com/world/rss',true,false,'Discovery only; publisher rights unverified'),
('Reuters via Google News','US','https://news.google.com/rss/search?q=when:24h+site:reuters.com&ceid=US:en&hl=en-US&gl=US',true,false,'Google News discovery, NOT a Reuters licensed feed; link only'),
('AP via Google News','US','https://news.google.com/rss/search?q=when:24h+site:apnews.com&hl=en-US&gl=US&ceid=US:en',true,false,'Google News discovery, NOT an AP licensed feed; link only')
ON CONFLICT(feed_url) DO NOTHING;
CREATE TABLE IF NOT EXISTS site_settings (key text PRIMARY KEY,value text NOT NULL);
-- Verified Egyptian RSS discovery endpoints, November 2026 (independently reported active).
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('Youm7 Egyptian Reports','EG','https://www.youm7.com/rss/SectionRss?SectionID=97',true,false,'RSS discovery; source copyright retained'),
('Al Masry Al Youm Egypt','EG','https://www.almasryalyoum.com/rss/rssfeeds?sectionId=3',true,false,'RSS discovery; source copyright retained'),
('Masrawy Egypt','EG','https://www.masrawy.com/rss/feed/35/%D8%A3%D8%AE%D8%A8%D8%A7%D8%B1%20%D9%85%D8%B5%D8%B1-%D8%B3%D9%8A%D8%A7%D8%B3%D8%A9',true,false,'RSS discovery; source copyright retained')
ON CONFLICT(feed_url) DO NOTHING;
CREATE TABLE IF NOT EXISTS editorial_features (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 kind text NOT NULL CHECK(kind IN ('article','special_file','news')),
 title text NOT NULL,body text NOT NULL,excerpt text,cover_url text,
 published boolean NOT NULL DEFAULT false,created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE editorial_features DROP CONSTRAINT IF EXISTS editorial_features_kind_check;
ALTER TABLE editorial_features ADD CONSTRAINT editorial_features_kind_check CHECK(kind IN ('article','special_file','news'));
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
    public async Task<SiteSettingsInput> SiteSettings(CancellationToken ct)
    {
        var defaults=new List<SiteCategory>{new("general","الرئيسية","Home","🇪🇬"),new("politics","سياسة","Politics","🌐"),new("economy","اقتصاد","Economy","📊"),new("sports","رياضة","Sports","⚽"),new("technology","تكنولوجيا","Technology","💻"),new("health","صحة","Health","⚕️"),new("education","تعليم","Education","🎓"),new("arts","فن","Arts","🎭")};
        if(!Configured)return new SiteSettingsInput(null,defaults);
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("SELECT key,value FROM site_settings WHERE key IN ('logo_url','categories')",conn);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        string? logo=null;List<SiteCategory>? categories=null;
        while(await reader.ReadAsync(ct))
        {
            if(reader.GetString(0)=="logo_url")logo=reader.GetString(1);
            else try{categories=System.Text.Json.JsonSerializer.Deserialize<List<SiteCategory>>(reader.GetString(1));}catch(System.Text.Json.JsonException){}
        }
        return new SiteSettingsInput(logo,categories is {Count:>0}?categories:defaults);
    }
    public async Task SaveSiteSettings(SiteSettingsInput input,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var tx=await conn.BeginTransactionAsync(ct);
        foreach(var item in new[]{new KeyValuePair<string,string>("logo_url",input.LogoUrl??""),new KeyValuePair<string,string>("categories",System.Text.Json.JsonSerializer.Serialize(input.Categories))})
        {
            await using var cmd=new NpgsqlCommand("INSERT INTO site_settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=EXCLUDED.value",conn,tx);
            cmd.Parameters.AddWithValue("k",item.Key);cmd.Parameters.AddWithValue("v",item.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task<object> Features(bool includeDrafts,CancellationToken ct)
    {
        if (!Configured) return Array.Empty<object>();
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"SELECT id,kind,title,body,excerpt,cover_url,published,created_at FROM editorial_features WHERE (@drafts OR published=true) ORDER BY created_at DESC LIMIT 100",conn);
        cmd.Parameters.AddWithValue("drafts",includeDrafts);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        var rows=new List<object>();
        while(await r.ReadAsync(ct)) rows.Add(new {id=r.GetInt64(0),kind=r.GetString(1),title=r.GetString(2),body=r.GetString(3),excerpt=r.IsDBNull(4)?null:r.GetString(4),coverUrl=r.IsDBNull(5)?null:r.GetString(5),published=r.GetBoolean(6),createdAt=r.GetDateTime(7)});
        return rows;
    }
    public async Task<long> SaveFeature(FeatureInput input,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"INSERT INTO editorial_features(kind,title,body,excerpt,cover_url,published)
SELECT @kind,@title,@body,@excerpt,@cover,@published WHERE @id=0
ON CONFLICT DO NOTHING",conn);
        cmd.Parameters.AddWithValue("id",input.Id.GetValueOrDefault());
        cmd.Parameters.AddWithValue("kind",input.Kind);
        cmd.Parameters.AddWithValue("title",input.Title.Trim());
        cmd.Parameters.AddWithValue("body",input.Body.Trim());
        cmd.Parameters.AddWithValue("excerpt",(object?)input.Excerpt??DBNull.Value);
        cmd.Parameters.AddWithValue("cover",(object?)input.CoverUrl??DBNull.Value);
        cmd.Parameters.AddWithValue("published",input.Published);
        if(input.Id.GetValueOrDefault()>0)
        {
            cmd.CommandText=@"UPDATE editorial_features SET kind=@kind,title=@title,body=@body,excerpt=@excerpt,cover_url=@cover,published=@published,updated_at=now() WHERE id=@id RETURNING id";
        }
        else cmd.CommandText=@"INSERT INTO editorial_features(kind,title,body,excerpt,cover_url,published) VALUES(@kind,@title,@body,@excerpt,@cover,@published) RETURNING id";
        var result=await cmd.ExecuteScalarAsync(ct);
        return result is long id?id:throw new InvalidOperationException("Feature not found");
    }
    public async Task<bool> DeleteFeature(long id,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("DELETE FROM editorial_features WHERE id=@id",conn);
        cmd.Parameters.AddWithValue("id",id);
        return await cmd.ExecuteNonQueryAsync(ct)>0;
    }
    public async Task<object> News(string? country,string? category,CancellationToken ct)
    {
        if (!Configured) return Array.Empty<object>();
        await using var conn = await Open(ct);
        await using var cmd = new NpgsqlCommand(@"SELECT i.id,COALESCE(NULLIF(i.title_ar,''),i.title),i.subject,f.name,i.original_url,i.author_name,i.original_published_at,i.image_count,i.country_code,i.category,CASE WHEN i.country_code='EG' THEN 'egyptian' WHEN i.country_code IN ('SA','AE','QA','KW','BH','OM','JO','LB','SY','IQ','YE','PS','MA','DZ','TN','LY','SD','MR','SO','DJ','KM') THEN 'arab' ELSE 'world' END AS region,CASE WHEN f.license_notes ILIKE '%IMAGE_EMBED_OK%' THEN i.source_image_url ELSE NULL END AS display_image_url
FROM news_items i JOIN news_feeds f ON f.id=i.feed_id WHERE i.status='published'
AND (@country IS NULL OR i.country_code=@country)
AND (@category IS NULL OR i.category=@category)
ORDER BY i.original_published_at DESC LIMIT 300", conn);
        cmd.Parameters.Add("country", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)(string.IsNullOrWhiteSpace(country)?null:country.ToUpperInvariant())??DBNull.Value;
        cmd.Parameters.Add("category", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)(string.IsNullOrWhiteSpace(category)?null:category.ToLowerInvariant())??DBNull.Value;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await r.ReadAsync(ct)) rows.Add(new { id=r.GetInt64(0), title=r.GetString(1), subject=r.IsDBNull(2)?null:r.GetString(2), source=r.GetString(3), url=r.GetString(4), author=r.IsDBNull(5)?null:r.GetString(5), publishedAt=r.GetDateTime(6), imageCount=r.GetInt32(7), country=r.IsDBNull(8)?null:r.GetString(8).Trim(),category=r.GetString(9),region=r.GetString(10),imageUrl=r.IsDBNull(11)?null:r.GetString(11) });
        return rows;
    }
    public async Task<object> Status(CancellationToken ct)
    {
        if (!Configured) return new { configured=false, feeds=Array.Empty<object>(), total=0, pending=0, published=0 };
        await using var conn=await Open(ct);
        var feeds=new List<object>();
        var sourceCounts=new Dictionary<long,(long Total,long Pending,long Published,long Rejected)>();
        await using (var byFeed=new NpgsqlCommand("SELECT feed_id,count(*),count(*) FILTER (WHERE status='pending'),count(*) FILTER (WHERE status='published'),count(*) FILTER (WHERE status='rejected') FROM news_items GROUP BY feed_id",conn))
        await using (var reader=await byFeed.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct))
                sourceCounts[reader.GetInt64(0)]=(reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4));
        await using (var cmd=new NpgsqlCommand("SELECT id,name,country_code,feed_url,enabled,auto_publish_allowed,license_notes,last_checked,last_error FROM news_feeds ORDER BY id",conn))
        await using (var r=await cmd.ExecuteReaderAsync(ct))
            while(await r.ReadAsync(ct))
            {
                var id=r.GetInt64(0);
                sourceCounts.TryGetValue(id,out var counts);
                feeds.Add(new {id,name=r.GetString(1),country=r.GetString(2),url=r.GetString(3),enabled=r.GetBoolean(4),autoPublish=r.GetBoolean(5),license=r.GetString(6),lastChecked=r.IsDBNull(7)?(DateTime?)null:r.GetDateTime(7),error=r.IsDBNull(8)?null:r.GetString(8),total=counts.Total,pending=counts.Pending,published=counts.Published,rejected=counts.Rejected});
            }
        await using var count=new NpgsqlCommand("SELECT count(*),count(*) FILTER (WHERE status='pending'),count(*) FILTER (WHERE status='published') FROM news_items",conn);
        await using var cr=await count.ExecuteReaderAsync(ct);
        await cr.ReadAsync(ct);
        return new { configured=true, feeds, total=cr.GetInt64(0), pending=cr.GetInt64(1), published=cr.GetInt64(2) };
    }
    public async Task<object> AdminArticles(CancellationToken ct)
    {
        if (!Configured) return Array.Empty<object>();
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("SELECT i.id,i.title,f.name,i.author_name,i.status,i.original_url,i.original_published_at,i.image_count,i.country_code,i.category,CASE WHEN i.country_code='EG' THEN 'egyptian' WHEN i.country_code IN ('SA','AE','QA','KW','BH','OM','JO','LB','SY','IQ','YE','PS','MA','DZ','TN','LY','SD','MR','SO','DJ','KM') THEN 'arab' ELSE 'world' END AS region,i.title_ar,i.subject,i.ai_provider,i.ai_processed_at,i.rss_excerpt,i.source_image_url FROM news_items i JOIN news_feeds f ON f.id=i.feed_id ORDER BY i.original_published_at DESC LIMIT 300",conn);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        var rows=new List<object>();
        while(await r.ReadAsync(ct)) rows.Add(new {id=r.GetInt64(0),title=r.GetString(1),source=r.GetString(2),author=r.IsDBNull(3)?null:r.GetString(3),status=r.GetString(4),url=r.GetString(5),publishedAt=r.GetDateTime(6),images=r.GetInt32(7),country=r.IsDBNull(8)?null:r.GetString(8).Trim(),category=r.GetString(9),region=r.GetString(10),titleAr=r.IsDBNull(11)?null:r.GetString(11),subject=r.IsDBNull(12)?null:r.GetString(12),aiProvider=r.IsDBNull(13)?null:r.GetString(13),aiProcessedAt=r.IsDBNull(14)?(DateTime?)null:r.GetDateTime(14),rssExcerpt=r.IsDBNull(15)?null:r.GetString(15),sourceImageUrl=r.IsDBNull(16)?null:r.GetString(16)});
        return rows;
    }
    public async Task<List<(long Id,string Title,string Source,string Country,string? Context)>> GetPendingAiBatch(int max,CancellationToken ct)
    {
        var rows=new List<(long,string,string,string,string?)>();
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"SELECT i.id,i.title,f.name,COALESCE(i.country_code,f.country_code),i.rss_excerpt FROM news_items i JOIN news_feeds f ON f.id=i.feed_id
WHERE i.status IN ('pending','published')
AND (NULLIF(btrim(i.title_ar),'') IS NULL OR NULLIF(btrim(i.subject),'') IS NULL)
AND (i.ai_processed_at IS NULL OR i.ai_processed_at < now() - interval '1 day')
ORDER BY i.imported_at LIMIT @max",conn);
        cmd.Parameters.AddWithValue("max",Math.Clamp(max,1,10));
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) rows.Add((reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3).Trim(),reader.IsDBNull(4)?null:reader.GetString(4)));
        return rows;
    }
    public async Task<(string Title,string Source,string Country,string? Context)?> GetForAi(long id,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"SELECT i.title,f.name,COALESCE(i.country_code,f.country_code),i.rss_excerpt FROM news_items i JOIN news_feeds f ON f.id=i.feed_id WHERE i.id=@id AND i.status IN ('pending','published')",conn);
        cmd.Parameters.AddWithValue("id",id);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0),r.GetString(1),r.GetString(2).Trim(),r.IsDBNull(3)?null:r.GetString(3)) : null;
    }
    public async Task SaveAiDraft(long id,ArabicNewsDraft draft,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"UPDATE news_items SET title_ar=COALESCE(NULLIF(btrim(title_ar),''),@title),subject=COALESCE(NULLIF(btrim(subject),''),@summary),category=@category,country_code=@country,ai_provider=@provider,ai_model=@model,ai_processed_at=now() WHERE id=@id AND status IN ('pending','published')",conn);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("title",draft.TitleAr);
        cmd.Parameters.AddWithValue("summary",draft.SummaryAr);
        cmd.Parameters.AddWithValue("category",draft.Category);
        cmd.Parameters.AddWithValue("country",draft.CountryCode);
        cmd.Parameters.AddWithValue("provider",draft.Provider);
        cmd.Parameters.AddWithValue("model",draft.Model);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> EditArticle(long id,ArticleEdit edit,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"UPDATE news_items SET title_ar=@title,subject=@body,country_code=@country,category=@category WHERE id=@id",conn);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("title",edit.TitleAr.Trim());
        cmd.Parameters.AddWithValue("body",(object?)edit.Subject??DBNull.Value);
        cmd.Parameters.AddWithValue("country",edit.CountryCode.ToUpperInvariant());
        cmd.Parameters.AddWithValue("category",edit.Category);
        return await cmd.ExecuteNonQueryAsync(ct)>0;
    }
    public async Task<bool> ChangeStatus(long id,string status,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand(@"UPDATE news_items SET status=@status,published_at=CASE WHEN @status='published' THEN COALESCE(published_at,now()) ELSE NULL END WHERE id=@id AND (@status<>'published' OR NULLIF(title_ar,'') IS NOT NULL)",conn);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("status",status);
        return await cmd.ExecuteNonQueryAsync(ct)>0;
    }
    public async Task<object> PublishReady(CancellationToken ct)
    {
        await using var conn=await Open(ct);
        // Admin-confirmed batch publication follows the same readiness rule as individual publication.
        // Source links and attribution remain attached to every item; no full publisher article is copied.
        await using var cmd=new NpgsqlCommand(@"UPDATE news_items SET status='published',published_at=COALESCE(published_at,now())
WHERE status='pending' AND NULLIF(btrim(title_ar),'') IS NOT NULL
AND NULLIF(btrim(original_url),'') IS NOT NULL",conn);
        var published=await cmd.ExecuteNonQueryAsync(ct);
        await using var count=new NpgsqlCommand(@"SELECT count(*) FILTER (WHERE status='pending'),
count(*) FILTER (WHERE status='pending' AND NULLIF(btrim(title_ar),'') IS NOT NULL AND NULLIF(btrim(original_url),'') IS NOT NULL)
FROM news_items",conn);
        await using var r=await count.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new {published,remaining=r.GetInt64(0),ready=r.GetInt64(1),policy="Explicit admin confirmation publishes Arabic-titled summaries linking to the original publisher; do not reproduce unlicensed full articles or images"};
    }
    public async Task<bool> DeleteArticle(long id,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("DELETE FROM news_items WHERE id=@id",conn);
        cmd.Parameters.AddWithValue("id",id);
        return await cmd.ExecuteNonQueryAsync(ct)>0;
    }
    public async Task<bool> EditSource(long id,SourceEdit edit,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("UPDATE news_feeds SET enabled=@enabled,auto_publish_allowed=@auto WHERE id=@id",conn);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("enabled",edit.Enabled);cmd.Parameters.AddWithValue("auto",edit.AutoPublishAllowed);
        return await cmd.ExecuteNonQueryAsync(ct)>0;
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
            await using var cmd=new NpgsqlCommand(@"INSERT INTO news_items(feed_id,title,title_ar,subject,original_url,author_name,original_published_at,status,published_at,country_code,category,rss_excerpt,source_image_url)
VALUES(@f,@t,@titleAr,@s,@u,@a,@p,@status,@published,(SELECT country_code FROM news_feeds WHERE id=@f),@category,@excerpt,@image) ON CONFLICT(original_url) DO UPDATE SET title_ar=COALESCE(NULLIF(news_items.title_ar,''),EXCLUDED.title_ar),rss_excerpt=COALESCE(news_items.rss_excerpt,EXCLUDED.rss_excerpt),source_image_url=COALESCE(news_items.source_image_url,EXCLUDED.source_image_url)",conn);
            cmd.Parameters.AddWithValue("f",feedId);
            cmd.Parameters.AddWithValue("t",item.Title);
            cmd.Parameters.AddWithValue("titleAr",LooksArabic(item.Title)?item.Title:DBNull.Value);
            // Do not copy full article bodies. Excerpts require publisher permission and are not imported by default.
            cmd.Parameters.AddWithValue("s",DBNull.Value);
            cmd.Parameters.AddWithValue("u",item.Url);
            cmd.Parameters.AddWithValue("a",(object?)item.Author??DBNull.Value);
            cmd.Parameters.AddWithValue("p",item.Published.UtcDateTime);
            cmd.Parameters.AddWithValue("category", Classify(item.Title));
            cmd.Parameters.AddWithValue("excerpt",(object?)item.Excerpt??DBNull.Value);
            cmd.Parameters.AddWithValue("image",(object?)item.ImageUrl??DBNull.Value);
            cmd.Parameters.AddWithValue("status",autoPublish?"published":"pending");
            cmd.Parameters.AddWithValue("published",autoPublish?DateTime.UtcNow:DBNull.Value);
            total+=await cmd.ExecuteNonQueryAsync(ct);
        }
        return total;
    }
    private static bool LooksArabic(string title)
    {
        var arabic=title.Count(c=>c is >= '\u0621' and <= '\u064A');
        var latin=title.Count(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return arabic>=3 && arabic>latin;
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
                        if(date<now.AddHours(-24)||date>now.AddMinutes(5)) continue;
                        if(string.IsNullOrWhiteSpace(title)||title.Length>500||!Uri.TryCreate(url,UriKind.Absolute,out var link)||link.Scheme!="https") continue;
                        var author=Get("creator")??Get("author");
                        var rawExcerpt=Get("description")??Get("summary");
                        var excerpt=string.IsNullOrWhiteSpace(rawExcerpt)?null:System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(rawExcerpt, "<[^>]+>", " ")).Trim();
                        if(excerpt?.Length>1600) excerpt=excerpt[..1600];
                        var image=entry.Descendants().FirstOrDefault(e=>(e.Name.LocalName is "thumbnail" or "content") && e.Attribute("url")!=null && (e.Name.NamespaceName.Contains("media")||e.Name.LocalName=="thumbnail"))?.Attribute("url")?.Value;
                        image??=entry.Elements().FirstOrDefault(e=>e.Name.LocalName=="enclosure" && (e.Attribute("type")?.Value.StartsWith("image/")==true))?.Attribute("url")?.Value;
                        if(!Uri.TryCreate(image,UriKind.Absolute,out var imageUri)||imageUri.Scheme!="https") image=null;
                        items.Add(new FeedItem(title,link.ToString(),author,date,excerpt,image));
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
