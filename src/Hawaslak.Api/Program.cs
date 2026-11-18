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
app.MapPost("/api/admin/sources/check-all", async (HttpRequest request, NewsStore store, IHttpClientFactory clients, CancellationToken ct) =>
{
    if(!Authorized(request))return Results.Unauthorized();
    var feeds=await store.AllFeedsForDiagnostics(ct);
    using var throttle=new SemaphoreSlim(5);
    var checks=await Task.WhenAll(feeds.Select(async feed=>{
        await throttle.WaitAsync(ct);
        try{
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response=await clients.CreateClient("rss").GetAsync(feed.Url,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode)return new {feed.Id,feed.Name,feed.Url,ok=false,http=(int)response.StatusCode,items=0,error="HTTP "+(int)response.StatusCode};
            if(response.Content.Headers.ContentLength>2_000_000)return new {feed.Id,feed.Name,feed.Url,ok=false,http=(int)response.StatusCode,items=0,error="Feed exceeds 2 MB"};
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
            var xml=await XDocument.LoadAsync(stream,LoadOptions.None,timeout.Token);
            var entries=xml.Descendants().Count(x=>x.Name.LocalName is "item" or "entry");
            var isFeed=xml.Root?.Name.LocalName is "rss" or "feed" or "RDF";
            return new {feed.Id,feed.Name,feed.Url,ok=isFeed&&entries>0,http=(int)response.StatusCode,items=entries,error=isFeed?(entries>0?"":"No items in RSS"):"Response is not an RSS/Atom feed"};
        }catch(Exception ex) when(ex is not OperationCanceledException || !ct.IsCancellationRequested){
            return new {feed.Id,feed.Name,feed.Url,ok=false,http=0,items=0,error=ex.GetType().Name+": "+ex.Message[..Math.Min(130,ex.Message.Length)]};
        }finally{throttle.Release();}
    }));
    if(request.Query["activate"]=="true")await store.ActivateVerifiedFeeds(checks.Where(x=>x.ok).Select(x=>x.Id).ToArray(),ct);
    return Results.Ok(new {total=checks.Length,working=checks.Count(x=>x.ok),failed=checks.Count(x=>!x.ok),activated=request.Query["activate"]=="true"?checks.Count(x=>x.ok):0,checkedAt=DateTimeOffset.UtcNow,checks});
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
        edit.CountryCode?.Length != 2 || !new[] {"general","politics","economy","sports","technology","health","education","arts","literature","crime"}.Contains(edit.Category))
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
 if(!string.IsNullOrWhiteSpace(input.LogoUrl)){
 var isImageData=System.Text.RegularExpressions.Regex.IsMatch(input.LogoUrl,@"^data:image/(png|jpeg|webp);base64,[A-Za-z0-9+/]+={0,2}$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
 var isHttps=Uri.TryCreate(input.LogoUrl,UriKind.Absolute,out var logoUri)&&logoUri.Scheme=="https"&&input.LogoUrl.Length<=1000;
 if(!isHttps&&!(isImageData&&input.LogoUrl.Length<=300000))return Results.BadRequest(new {error="Logo must be HTTPS URL or PNG/JPEG/WebP image up to 220 KB"});
}
 if(new[]{input.BannerTitle,input.BannerSubtitle,input.BannerButtonText}.Any(x=>x?.Length>300)||input.BannerLink?.Length>1500)return Results.BadRequest(new {error="Banner text or link too long"});
 if(!string.IsNullOrWhiteSpace(input.BannerLink)&&!(Uri.TryCreate(input.BannerLink,UriKind.Absolute,out var bannerLink)&&bannerLink.Scheme=="https"))return Results.BadRequest(new {error="Banner link must use HTTPS"});
 if(!string.IsNullOrWhiteSpace(input.BannerImageUrl)){
 var imageData=System.Text.RegularExpressions.Regex.IsMatch(input.BannerImageUrl,@"^data:image/(png|jpeg|webp);base64,[A-Za-z0-9+/]+={0,2}$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
 var imageHttps=Uri.TryCreate(input.BannerImageUrl,UriKind.Absolute,out var imageUri)&&imageUri.Scheme=="https"&&input.BannerImageUrl.Length<=1500;
 if(!imageHttps&&!(imageData&&input.BannerImageUrl.Length<=1500000))return Results.BadRequest(new {error="Banner image must be HTTPS or PNG/JPEG/WebP under 1 MB"});
 }
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
record SiteSettingsInput(string? LogoUrl,List<SiteCategory> Categories,string? BannerTitle=null,string? BannerSubtitle=null,string? BannerImageUrl=null,string? BannerLink=null,string? BannerButtonText=null);
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
-- Additional Egyptian specialist RSS discovery candidates; check accessibility and permissions in source diagnostics.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('FilGoal Sports','EG','https://www.filgoal.com/rss',true,false,'Egyptian sports candidate; verify feed and publisher permissions'),
('Yallakora Sports','EG','https://www.yallakora.com/rss',true,false,'Egyptian sports candidate; verify feed and publisher permissions'),
('Kooora Egyptian Sports','EG','https://www.kooora.com/rss',true,false,'Sports candidate; verify country attribution, feed and permissions'),
('Masrawy Arts','EG','https://www.masrawy.com/rss/feed/19/%D9%81%D9%86%D9%88%D9%86',true,false,'Egyptian arts candidate; verify feed and publisher permissions'),
('Youm7 Arts','EG','https://www.youm7.com/rss/SectionRss?SectionID=48',true,false,'Egyptian arts candidate; verify section feed and publisher permissions'),
('Youm7 Health','EG','https://www.youm7.com/rss/SectionRss?SectionID=245',true,false,'Egyptian health candidate; verify section feed and publisher permissions'),
('Masrawy Health','EG','https://www.masrawy.com/rss/feed/18/%D8%B5%D8%AD%D8%A9',true,false,'Egyptian health candidate; verify feed and publisher permissions'),
('Al Ahram Sports','EG','https://gate.ahram.org.eg/rss/13.aspx',true,false,'Egyptian sports candidate; verify feed and publisher permissions'),
('Al Ahram Arts','EG','https://gate.ahram.org.eg/rss/7.aspx',true,false,'Egyptian arts candidate; verify feed and publisher permissions'),
('Al Ahram Health','EG','https://gate.ahram.org.eg/rss/4.aspx',true,false,'Egyptian health candidate; verify feed and publisher permissions')
ON CONFLICT(feed_url) DO NOTHING;
-- Egyptian publishers across general news, sports, business, culture and local reporting.
-- Feed URLs listed in public RSS directories; validate each through admin check-all before relying on it.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('Egypt Independent','EG','https://www.egyptindependent.com/feed/',true,false,'RSS directory candidate; verify connection and rights'),
('Daily News Egypt','EG','https://www.dailynewsegypt.com/feed/',true,false,'RSS directory candidate; verify connection and rights'),
('Egyptian Streets','EG','https://egyptianstreets.com/feed/',true,false,'RSS directory candidate; verify connection and rights'),
('KingFut Sports','EG','https://feeds.feedburner.com/KingFut',true,false,'Egyptian specialist sports candidate; verify connection and rights'),
('Egypt Oil and Gas','EG','https://egyptoil-gas.com/news/feed/',true,false,'Egyptian specialist energy candidate; verify connection and rights'),
('Mada Masr','EG','https://www.madamasr.com/en/feed/',true,false,'Egyptian independent reporting candidate; verify connection and rights'),
('Egyptian Gazette','EG','https://egyptian-gazette.com/feed/',true,false,'Egyptian cross-section reporting candidate; verify connection and rights'),
('Sada El Balad','EG','https://www.elbalad.news/rss.aspx',true,false,'Egyptian cross-section RSS candidate; verify connection and rights'),
('Al Dostor','EG','https://www.dostor.org/rss.aspx',true,false,'Egyptian cross-section RSS candidate; verify connection and rights'),
('Misr5','EG','https://misr5.com/feed/',true,false,'Egyptian cross-section RSS candidate; verify connection and rights'),
('Al Ahaly','EG','https://alahalygate.com/feed/',true,false,'Egyptian news and opinion candidate; verify connection and rights'),
('El Zaman','EG','https://www.elzmannews.com/rss.xml',true,false,'Egyptian news candidate; verify connection and rights'),
('Nile Sports','EG','https://nilesports.com/feed/',true,false,'Egyptian specialist sports candidate; verify connection and rights'),
('Al Sabah','EG','https://www.sabaharabi.com/rss.xml',true,false,'Egyptian reporting candidate; verify connection and rights'),
('Youm7 Sports','EG','https://www.youm7.com/rss/SectionRss?SectionID=298',true,false,'Egyptian sports section candidate; verify connection and rights')
ON CONFLICT(feed_url) DO NOTHING;
-- Country-specific domestic publisher candidates. These are not live-verified.
-- All stay editorial-review-only; admin diagnostics determines reachability.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('TOLOnews — Afghanistan','AF','https://tolonews.com/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Exit News — Albania','AL','https://exit.al/en/feed/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('TSA Algerie — Algeria','DZ','https://www.tsa-algerie.com/feed/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('La Nacion — Argentina','AR','https://www.lanacion.com.ar/arc/outboundfeeds/rss/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('News.am — Armenia','AM','https://news.am/eng/rss/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('ABC News Australia — Australia','AU','https://www.abc.net.au/news/feed/51120/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Der Standard — Austria','AT','https://www.derstandard.at/rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Trend News — Azerbaijan','AZ','https://en.trend.az/feeds/index.rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Dhaka Tribune — Bangladesh','BD','https://www.dhakatribune.com/feed',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('The Brussels Times — Belgium','BE','https://www.brusselstimes.com/feed',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Agencia Brasil — Brazil','BR','https://agenciabrasil.ebc.com.br/rss/ultimasnoticias/feed.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('CBC News — Canada','CA','https://www.cbc.ca/cmlink/rss-topstories',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('BioBioChile — Chile','CL','https://www.biobiochile.cl/lista/categorias/nacional/feed',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('El Tiempo — Colombia','CO','https://www.eltiempo.com/rss/colombia.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Index.hr — Croatia','HR','https://www.index.hr/rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Radio Prague International — Czechia','CZ','https://english.radio.cz/rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('The Copenhagen Post — Denmark','DK','https://cphpost.dk/feed/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Franceinfo — France','FR','https://www.francetvinfo.fr/titres.rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Civil Georgia — Georgia','GE','https://civil.ge/feed',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Tagesschau — Germany','DE','https://www.tagesschau.de/xml/rss2',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('GhanaWeb — Ghana','GH','https://www.ghanaweb.com/GhanaHomePage/rss/news.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Kathimerini — Greece','GR','https://www.ekathimerini.com/feed/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('The Hindu — India','IN','https://www.thehindu.com/news/national/feeder/default.rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Jakarta Post — Indonesia','ID','https://www.thejakartapost.com/rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('RTE News — Ireland','IE','https://www.rte.ie/feeds/rss/?index=/news/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('ANSA — Italy','IT','https://www.ansa.it/sito/ansait_rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('NHK World Japan — Japan','JP','https://www3.nhk.or.jp/nhkworld/en/news/feeds/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('The Standard Kenya — Kenya','KE','https://www.standardmedia.co.ke/rss/headlines.php',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Malay Mail — Malaysia','MY','https://www.malaymail.com/feed/rss/malaysia',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('El Universal — Mexico','MX','https://www.eluniversal.com.mx/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Hespress — Morocco','MA','https://www.hespress.com/feed',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('NL Times — Netherlands','NL','https://nltimes.nl/rssfeed2',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('RNZ News — New Zealand','NZ','https://www.rnz.co.nz/rss/national.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Premium Times Nigeria — Nigeria','NG','https://www.premiumtimesng.com/feed',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('NRK — Norway','NO','https://www.nrk.no/toppsaker.rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Dawn — Pakistan','PK','https://www.dawn.com/feeds/home',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Philippine Star — Philippines','PH','https://www.philstar.com/rss/headlines',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('TVN24 — Poland','PL','https://tvn24.pl/najnowsze.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Publico — Portugal','PT','https://feeds.feedburner.com/PublicoRSS',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Romania Insider — Romania','RO','https://www.romania-insider.com/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Saudi Gazette — Saudi Arabia','SA','https://saudigazette.com.sa/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('CNA Singapore — Singapore','SG','https://www.channelnewsasia.com/api/v1/rss-outbound-feed?_format=xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('News24 — South Africa','ZA','https://feeds.news24.com/articles/news24/TopStories/rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Korea Herald — South Korea','KR','https://www.koreaherald.com/rss/020000000000.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('El Pais — Spain','ES','https://feeds.elpais.com/mrss-s/pages/ep/site/elpais.com/portada',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Daily Mirror Sri Lanka — Sri Lanka','LK','https://www.dailymirror.lk/rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('SVT Nyheter — Sweden','SE','https://www.svt.se/nyheter/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Swissinfo — Switzerland','CH','https://www.swissinfo.ch/eng/rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Bangkok Post — Thailand','TH','https://www.bangkokpost.com/rss/data/topstories.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Hurriyet — Turkey','TR','https://www.hurriyet.com.tr/rss/anasayfa',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('Ukrinform — Ukraine','UA','https://www.ukrinform.net/rss/block-lastnews',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('The National UAE — United Arab Emirates','AE','https://www.thenationalnews.com/arc/outboundfeeds/rss/',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('BBC UK News — United Kingdom','GB','https://feeds.bbci.co.uk/news/uk/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('NPR National — United States','US','https://feeds.npr.org/1003/rss.xml',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('VnExpress — Vietnam','VN','https://vnexpress.net/rss/tin-moi-nhat.rss',true,false,'Domestic publisher candidate; RSS and rights must be verified'),
('NewsDay Zimbabwe — Zimbabwe','ZW','https://www.newsday.co.zw/feed',true,false,'Domestic publisher candidate; RSS and rights must be verified')
ON CONFLICT(feed_url) DO NOTHING;
-- Additional domestic news publishers. Candidate homepage domains only: /feed is a discovery guess,
-- NOT a verified RSS endpoint. Keep disabled until successful admin validation.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('Diari d''Andorra','AD','https://diariandorra.ad/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Jornal de Angola','AO','https://jornaldeangola.ao/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Antigua Observer','AG','https://antiguaobserver.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('The Nassau Guardian','BS','https://thenassauguardian.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Gulf Daily News','BH','https://gdnonline.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Nation News Barbados','BB','https://nationnews.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('BelTA','BY','https://eng.belta.by/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Amandala','BZ','https://amandala.com.bz/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Nation Benin','BJ','https://lanation.bj/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Kuensel','BT','https://kuenselonline.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Razon Bolivia','BO','https://la-razon.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Klix.ba','BA','https://klix.ba/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Mmegi','BW','https://mmegi.bw/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Borneo Bulletin','BN','https://borneobulletin.com.bn/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Novinite','BG','https://novinite.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('LeFaso.net','BF','https://lefaso.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Iwacu','BI','https://iwacu-burundi.org/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Khmer Times','KH','https://khmertimeskh.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Cameroon Tribune','CM','https://cameroon-tribune.cm/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('A Nacao','CV','https://anacao.cv/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Radio Ndeke Luka','CF','https://radiondekeluka.org/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Alwihda Info','TD','https://alwihdainfo.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('China Daily','CN','https://chinadaily.com.cn/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Al Watwan','KM','https://alwatwan.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Les Depeches de Brazzaville','CG','https://adiac-congo.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Radio Okapi','CD','https://radiookapi.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Nacion Costa Rica','CR','https://nacion.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Fraternite Matin','CI','https://fratmat.info/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Granma','CU','https://granma.cu/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Cyprus Mail','CY','https://cyprus-mail.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Nation Djibouti','DJ','https://lanation.dj/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Dominica News Online','DM','https://dominicanewsonline.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Diario Libre','DO','https://diariolibre.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('El Universo','EC','https://eluniverso.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Prensa Grafica','SV','https://laprensagrafica.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Ahora EG','GQ','https://ahoraeg.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Shabait','ER','https://shabait.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('ERR News','EE','https://news.err.ee/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Eswatini Observer','SZ','https://eswatiniobserver.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Addis Standard','ET','https://addisstandard.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Fiji Times','FJ','https://fijitimes.com.fj/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Yle News','FI','https://yle.fi/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Gabon Review','GA','https://gabonreview.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('The Point Gambia','GM','https://thepoint.gm/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Now Grenada','GD','https://nowgrenada.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Prensa Libre','GT','https://prensalibre.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Guinee Matin','GN','https://guineematin.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('O Democrata','GW','https://odemocratagb.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Stabroek News','GY','https://stabroeknews.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Le Nouvelliste','HT','https://lenouvelliste.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Prensa Honduras','HN','https://laprensa.hn/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Telex Hungary','HU','https://telex.hu/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('RUV','IS','https://ruv.is/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('IRNA','IR','https://en.irna.ir/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Shafaq News','IQ','https://shafaq.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Times of Israel','IL','https://timesofisrael.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Jamaica Gleaner','JM','https://jamaica-gleaner.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Jordan Times','JO','https://jordantimes.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Kazinform','KZ','https://inform.kz/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Kiribati Independent','KI','https://kiribatiindependent.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Koha Ditore','XK','https://koha.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Kuwait Times','KW','https://kuwaittimes.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('24.kg','KG','https://24.kg/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Vientiane Times','LA','https://vientianetimes.org.la/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('LSM Latvia','LV','https://eng.lsm.lv/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('L''Orient Today','LB','https://today.lorientlejour.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Lesotho Times','LS','https://lestimes.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('FrontPage Africa','LR','https://frontpageafricaonline.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Libya Observer','LY','https://libyaobserver.ly/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Liechtensteiner Vaterland','LI','https://vaterland.li/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('LRT Lithuania','LT','https://lrt.lt/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Luxembourg Times','LU','https://luxtimes.lu/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Madagascar Tribune','MG','https://madagascar-tribune.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Malawi 24','MW','https://malawi24.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Sun Online Maldives','MV','https://sun.mv/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Maliweb','ML','https://maliweb.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Times of Malta','MT','https://timesofmalta.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Marshall Islands Journal','MH','https://marshallislandsjournal.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Sahara Medias','MR','https://saharamedias.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('L''Express Mauritius','MU','https://lexpress.mu/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Marianas Variety','FM','https://marianasvariety.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('NewsMaker Moldova','MD','https://newsmaker.md/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Monaco Tribune','MC','https://monaco-tribune.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Montsame','MN','https://montsame.mn/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Vijesti Montenegro','ME','https://vijesti.me/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Club of Mozambique','MZ','https://clubofmozambique.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Myanmar Now','MM','https://myanmar-now.org/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('The Namibian','NA','https://namibian.com.na/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Nauru Bulletin','NR','https://naurugov.nr/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Kathmandu Post','NP','https://kathmandupost.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Prensa Nicaragua','NI','https://laprensani.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('ActuNiger','NE','https://actuniger.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('KCNA Watch','KP','https://kcnawatch.org/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('SDK.mk','MK','https://sdk.mk/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Times of Oman','OM','https://timesofoman.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Island Times Palau','PW','https://islandtimes.org/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('WAFA','PS','https://english.wafa.ps/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('La Prensa Panama','PA','https://prensa.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Post Courier PNG','PG','https://postcourier.com.pg/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('ABC Color','PY','https://abc.com.py/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('El Comercio Peru','PE','https://elcomercio.pe/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Gulf Times Qatar','QA','https://gulf-times.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('TASS','RU','https://tass.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('The New Times Rwanda','RW','https://newtimes.co.rw/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('St Kitts Nevis Observer','KN','https://thestkittsnevisobserver.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('St Lucia Times','LC','https://stluciatimes.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Searchlight SVG','VC','https://searchlight.vc/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Samoa Observer','WS','https://samoaobserver.ws/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('San Marino RTV','SM','https://sanmarinortv.sm/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Telas Non','ST','https://tela-non.st/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Le Soleil Senegal','SN','https://lesoleil.sn/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('N1 Serbia','RS','https://n1info.rs/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Seychelles Nation','SC','https://nation.sc/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Awoko','SL','https://awokonewspaper.sl/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('SME Slovakia','SK','https://sme.sk/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('RTV Slovenia','SI','https://rtvslo.si/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Solomon Star News','SB','https://solomonstarnews.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Hiiraan Online','SO','https://hiiraan.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Radio Tamazuj','SS','https://radiotamazuj.org/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Sudan Tribune','SD','https://sudantribune.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Starnieuws Suriname','SR','https://starnieuws.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Enab Baladi','SY','https://enabbaladi.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Asia Plus Tajikistan','TJ','https://asiaplustj.info/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('The Citizen Tanzania','TZ','https://thecitizen.co.tz/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Tatoli','TL','https://tatoli.tl/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Togo First','TG','https://togofirst.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Matangi Tonga','TO','https://matangitonga.to/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Trinidad Express','TT','https://trinidadexpress.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Mosaique FM','TN','https://mosaiquefm.net/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Turkmenportal','TM','https://turkmenportal.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Tuvalu Media','TV','https://tuvalumedia.tv/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Daily Monitor Uganda','UG','https://monitor.co.ug/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('El Pais Uruguay','UY','https://elpais.com.uy/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Gazeta.uz','UZ','https://gazeta.uz/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Vanuatu Daily Post','VU','https://dailypost.vu/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Vatican News','VA','https://vaticannews.va/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('El Nacional Venezuela','VE','https://elnacional.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Yemen Times','YE','https://yementimes.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights'),
('Lusaka Times','ZM','https://lusakatimes.com/feed',false,false,'UNVERIFIED feed-path guess; enable only after confirming publisher RSS endpoint and rights')
ON CONFLICT(feed_url) DO NOTHING;
-- Egyptian section feeds published by Zawia3; keep editorial approval and test reachability from Render.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('Zawia3 Egypt Politics','EG','https://zawia3.com/en/category/politics-en/feed/',true,false,'Publisher-listed RSS; editorial review required'),
('Zawia3 Egypt Economy','EG','https://zawia3.com/en/category/economics-en/feed/',true,false,'Publisher-listed RSS; editorial review required'),
('Zawia3 Egypt Culture','EG','https://zawia3.com/en/category/culture/feed/',true,false,'Publisher-listed RSS; editorial review required'),
('Zawia3 Egypt Investigations','EG','https://zawia3.com/en/category/investigations-en/feed/',true,false,'Publisher-listed RSS; editorial review required'),
('Zawia3 Egypt Society','EG','https://zawia3.com/en/category/society-en/feed/',true,false,'Publisher-listed RSS; editorial review required'),
('Zawia3 Egypt Environment','EG','https://zawia3.com/en/category/ُenv/feed/',false,false,'Publisher-listed RSS; Unicode path requires runtime verification'),
('Al Shorouk Egypt','EG','https://www.shorouknews.com/egypt/rss',true,false,'RSS directory recently reported working; editorial review required')
ON CONFLICT(feed_url) DO NOTHING;
-- Egyptian publisher RSS endpoints independently surfaced in November 2026.
-- Publisher links are still checked at runtime; never assume all hosts permit Render access.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('Youm7 Main RSS','EG','https://www.youm7.com/rss/rss',true,false,'Official publisher RSS listing; editorial approval required'),
('Youm7 Culture & Literature','EG','https://www.youm7.com/rss/SectionRss?SectionID=94',true,false,'Official publisher culture RSS; editorial approval required'),
('Al Masry Al Youm Main RSS','EG','https://www.almasryalyoum.com/rss/rssfeeds',true,false,'Third-party RSS reader reports working; editorial approval required')
ON CONFLICT(feed_url) DO NOTHING;
-- RSS links checked against current publisher output or actively monitored feed directories (Nov 2026).
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('Youm7 Technology Verified','EG','https://www.youm7.com/rss/SectionRss?SectionID=328',true,false,'Publisher feed content inspected Nov 2026; editorial review'),
('Youm7 Governorates Verified','EG','https://www.youm7.com/rss/SectionRss?SectionID=296',true,false,'Publisher feed content inspected Nov 2026; editorial review')
ON CONFLICT(feed_url) DO NOTHING;
-- Egyptian section feeds observed serving current entries in November 2026.
INSERT INTO news_feeds(name,country_code,feed_url,enabled,auto_publish_allowed,license_notes) VALUES
('Youm7 Technology','EG','https://www.youm7.com/rss/SectionRss?SectionID=328',true,false,'Current entries confirmed by feed reader and search; editorial approval required'),
('Al Shorouk Egypt News','EG','https://www.shorouknews.com/egypt/rss',true,false,'Publisher lists section RSS; third-party fetch returns RSS content type; editorial approval required')
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
        var defaults=new List<SiteCategory>{new("general","الرئيسية","Home","🇪🇬"),new("politics","سياسة","Politics","🌐"),new("economy","اقتصاد","Economy","📊"),new("sports","رياضة","Sports","⚽"),new("technology","تكنولوجيا","Technology","💻"),new("health","صحة","Health","⚕️"),new("education","تعليم","Education","🎓"),new("arts","فن","Arts","🎭"),new("literature","أدب","Literature","📚"),new("crime","الجريمة والعقاب","Crime & Justice","⚖️")};
        if(!Configured)return new SiteSettingsInput(null,defaults);
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("SELECT key,value FROM site_settings WHERE key IN ('logo_url','categories','banner_title','banner_subtitle','banner_image_url','banner_link','banner_button_text')",conn);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        string? logo=null;List<SiteCategory>? categories=null;var banner=new Dictionary<string,string>();
        while(await reader.ReadAsync(ct))
        {
            if(reader.GetString(0)=="logo_url")logo=reader.GetString(1);
            else if(reader.GetString(0)=="categories")try{categories=System.Text.Json.JsonSerializer.Deserialize<List<SiteCategory>>(reader.GetString(1));}catch(System.Text.Json.JsonException){} else banner[reader.GetString(0)]=reader.GetString(1);
        }
        if(categories is {Count:>0}&&!categories.Any(x=>x.Id=="literature"))categories.Add(new("literature","أدب","Literature","📚"));
        if(categories is {Count:>0}&&!categories.Any(x=>x.Id=="crime"))categories.Add(new("crime","الجريمة والعقاب","Crime & Justice","⚖️"));
        return new SiteSettingsInput(logo,categories is {Count:>0}?categories:defaults,banner.GetValueOrDefault("banner_title"),banner.GetValueOrDefault("banner_subtitle"),banner.GetValueOrDefault("banner_image_url"),banner.GetValueOrDefault("banner_link"),banner.GetValueOrDefault("banner_button_text"));
    }
    public async Task SaveSiteSettings(SiteSettingsInput input,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        await using var tx=await conn.BeginTransactionAsync(ct);
        foreach(var item in new[]{new KeyValuePair<string,string>("logo_url",input.LogoUrl??""),new KeyValuePair<string,string>("categories",System.Text.Json.JsonSerializer.Serialize(input.Categories)),new("banner_title",input.BannerTitle??""),new("banner_subtitle",input.BannerSubtitle??""),new("banner_image_url",input.BannerImageUrl??""),new("banner_link",input.BannerLink??""),new("banner_button_text",input.BannerButtonText??"")})
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
    public async Task ActivateVerifiedFeeds(long[] ids,CancellationToken ct)
    {
        if(ids.Length==0)return;
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("UPDATE news_feeds SET enabled=true,last_checked=now(),last_error=NULL WHERE id=ANY(@ids)",conn);
        cmd.Parameters.AddWithValue("ids",ids);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<List<(long Id,string Name,string Url)>> AllFeedsForDiagnostics(CancellationToken ct)
    {
        var feeds=new List<(long,string,string)>();
        await using var conn=await Open(ct);
        await using var cmd=new NpgsqlCommand("SELECT id,name,feed_url FROM news_feeds ORDER BY id",conn);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))feeds.Add((reader.GetInt64(0),reader.GetString(1),reader.GetString(2)));
        return feeds;
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
ORDER BY CASE WHEN i.status='published' AND NULLIF(btrim(i.subject),'') IS NULL THEN 0 WHEN NULLIF(btrim(i.title_ar),'') IS NULL THEN 1 ELSE 2 END, i.imported_at DESC LIMIT @max",conn);
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
AND NULLIF(btrim(original_url),'') IS NOT NULL AND original_url LIKE 'https://%'",conn);
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
    private static async Task<string?> FeedCategory(long feedId,NpgsqlConnection conn,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("SELECT feed_url FROM news_feeds WHERE id=@id",conn);
        cmd.Parameters.AddWithValue("id",feedId);
        var url=(await cmd.ExecuteScalarAsync(ct))?.ToString()??"";
        if(url.Contains("SectionID=328",StringComparison.OrdinalIgnoreCase))return "technology";
        if(url.Contains("SectionID=298",StringComparison.OrdinalIgnoreCase)||url.Contains("rss/13.aspx",StringComparison.OrdinalIgnoreCase))return "sports";
        if(url.Contains("SectionID=245",StringComparison.OrdinalIgnoreCase)||url.Contains("rss/4.aspx",StringComparison.OrdinalIgnoreCase))return "health";
        if(url.Contains("SectionID=48",StringComparison.OrdinalIgnoreCase)||url.Contains("rss/7.aspx",StringComparison.OrdinalIgnoreCase))return "arts";
        if(url.Contains("/category/politics-en/",StringComparison.OrdinalIgnoreCase))return "politics";
        if(url.Contains("/category/economics-en/",StringComparison.OrdinalIgnoreCase))return "economy";
        if(url.Contains("/category/culture/",StringComparison.OrdinalIgnoreCase)||url.Contains("SectionID=94",StringComparison.OrdinalIgnoreCase))return "literature";
        if(url.Contains("SectionID=328",StringComparison.OrdinalIgnoreCase))return "technology";
        if(url.Contains("SectionID=298",StringComparison.OrdinalIgnoreCase)||url.Contains("SectionID=13",StringComparison.OrdinalIgnoreCase))return "sports";
        if(url.Contains("/category/investigations-en/",StringComparison.OrdinalIgnoreCase))return "crime";
        return null;
    }
    public async Task<int> Import(long feedId,bool autoPublish,IEnumerable<FeedItem> items,CancellationToken ct)
    {
        await using var conn=await Open(ct);
        var total=0;
        var feedCategory=await FeedCategory(feedId,conn,ct);
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
            // Prefer a dedicated publisher section over title-keyword guessing.
            cmd.Parameters.AddWithValue("category", feedCategory??Classify(item.Title));
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
        if (new[]{"جريمة","جرائم","قتل","جنايات","النيابة العامة","المحكمة الجنائية","محكمة الجنايات","حبس المتهم","القبض على","سرقة","مخدرات","حكم قضائي","crime","criminal","murder","arrested","prosecution","sentenced","court verdict"}.Any(t.Contains)) return "crime";
        if (new[]{"رواية","روايات","شعر","شاعر","أدب","أديب","كتاب جديد","إصدار أدبي","قصة قصيرة","قصص قصيرة","literature","novel","poetry","poet","literary","book fair"}.Any(t.Contains)) return "literature";
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
