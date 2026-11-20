using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record AiProviderStatus(string Name, bool Configured, string? Model, int Priority);
public sealed record ArabicNewsDraft(string TitleAr, string SummaryAr, string Category, string CountryCode, string Provider, string Model);
public sealed record AiRequest(string Title, string? Context, string? Source, string? CountryCode);

public sealed class NewsAiService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<NewsAiService> _logger;
    private static readonly string[] Categories = ["general","politics","economy","sports","technology","health","education","arts","literature","crime"];
    public NewsAiService(IHttpClientFactory factory, ILogger<NewsAiService> logger) { _factory = factory; _logger = logger; }
    static string Env(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) ?? fallback;
    static bool Has(string key) => !string.IsNullOrWhiteSpace(Env(key));
    public AiProviderStatus[] Providers() =>
    [
        new("Cloudflare Workers AI",Has("CLOUDFLARE_API_TOKEN") && Has("CLOUDFLARE_ACCOUNT_ID"),Env("CLOUDFLARE_MODEL","@cf/meta/llama-3.1-8b-instruct"),1),
        new("GroqCloud",Has("GROQ_API_KEY"),Env("GROQ_MODEL","llama-3.3-70b-versatile"),2),
        new("Cerebras",Has("CEREBRAS_API_KEY"),Env("CEREBRAS_MODEL","gpt-oss-120b"),3),
        new("Google Gemini",Has("GEMINI_API_KEY"),Env("GEMINI_MODEL","gemini-2.5-flash"),4),
        new("Mistral AI",Has("MISTRAL_API_KEY"),Env("MISTRAL_MODEL","mistral-small-latest"),5),
        new("OpenRouter",Has("OPENROUTER_API_KEY"),Env("OPENROUTER_MODEL","openrouter/free"),6)
    ];
    public async Task<ArabicNewsDraft> Generate(AiRequest input,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(input.Title) || input.Title.Length>1000) throw new ArgumentException("Valid source title required");
        var prompt = "اكتب JSON فقط بالمفاتيح title_ar,summary_ar,category,country_code. العنوان بالعربية الفصحى واضح وموضوعي بحد أقصى 140 حرفا. اكتب ملخصًا صحفيًا بالعربية من 100 إلى 180 كلمة فقط إذا كان الوصف الأصلي يوفر تفاصيل كافية، في فقرات قصيرة واضحة. إذا كان المتاح عنوانًا فقط، فاكتب جملة أو جملتين قصيرتين فقط ولا تختلق أي وقائع أو خلفيات أو تصريحات أو أرقام. لا تكرر الجمل لإطالة النص. لا تضف أي تفاصيل غير موجودة في البيانات المقدمة. أعد اسم الدولة المعنية بالخبر برمز ISO من حرفين إذا كان واضحا، وإلا استعمل رمز الدولة الوارد. اختر category من general,politics,economy,sports,technology,health,education,arts,literature,crime. لا تنسخ المقالات، ولا تدعي الوصول إلى روابط خارجية.\nTitle: "+input.Title+"\nPermitted context: "+(input.Context??"")+"\nSource: "+(input.Source??"")+"\nDefault country: "+(input.CountryCode??"");
        var errors=new List<string>();
        foreach(var p in Providers().Where(p=>p.Configured))
        {
            try
            {
                var raw=await Call(p.Name,p.Model!,prompt,ct);
                var match=Regex.Match(raw,@"\{[\s\S]*?\}");
                if(!match.Success) throw new FormatException("AI response contained no JSON object (length "+raw.Length+")");
                using var doc=JsonDocument.Parse(match.Value);
                var root=doc.RootElement;
                static string Val(JsonElement el,string prop) => el.TryGetProperty(prop,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()?.Trim()??"":"";
                var title=Val(root,"title_ar");var summary=Val(root,"summary_ar");
                if(title.Length is < 8 or > 250 || summary.Length is < 15 or > 1500) throw new FormatException("Invalid AI response lengths");
                var cat=Val(root,"category").ToLowerInvariant();
                if(!Categories.Contains(cat)) cat="general";
                var country=Val(root,"country_code").ToUpperInvariant();
                if(!Regex.IsMatch(country, "^[A-Z]{2}$")) country=Regex.IsMatch(input.CountryCode??"", "^[A-Za-z]{2}$")?input.CountryCode!.ToUpperInvariant():"ZZ";
                return new(title,summary,cat,country,p.Name,p.Model!);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception e)
            {
                _logger.LogWarning("AI provider {Provider} unavailable: {Error}",p.Name,e.Message);
                errors.Add(p.Name + ": "+ e.Message[..Math.Min(90,e.Message.Length)]);
            }
        }
        throw new InvalidOperationException(errors.Count==0?"No AI API credentials configured. Set a provider key in Render.": "AI providers failed: "+string.Join(" | ",errors));
    }
    private async Task<string> Call(string provider,string model,string prompt,CancellationToken ct)
    {
        var client=_factory.CreateClient("ai");
        string url,key;
        object payload;
        if(provider=="Cloudflare Workers AI")
        {
            url="https://api.cloudflare.com/client/v4/accounts/"+Uri.EscapeDataString(Env("CLOUDFLARE_ACCOUNT_ID"))+"/ai/run/"+model;
            key=Env("CLOUDFLARE_API_TOKEN");
            payload=new {messages=new[]{new {role="system",content="Return exactly one valid JSON object with keys title_ar, summary_ar, category, country_code. No markdown or explanation."},new {role="user",content=prompt}},max_tokens=1800,temperature=0.1,response_format=new {type="json_object"}};
        }
        else
        {
            (url,key)=provider switch {
                "GroqCloud"=>("https://api.groq.com/openai/v1/chat/completions",Env("GROQ_API_KEY")),
                "Cerebras"=>("https://api.cerebras.ai/v1/chat/completions",Env("CEREBRAS_API_KEY")),
                "Google Gemini"=>("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",Env("GEMINI_API_KEY")),
                "Mistral AI"=>("https://api.mistral.ai/v1/chat/completions",Env("MISTRAL_API_KEY")),
                _=>("https://openrouter.ai/api/v1/chat/completions",Env("OPENROUTER_API_KEY"))};
            payload=new {model,messages=new[]{new {role="user",content=prompt}},max_tokens=1200,temperature=0.15};
        }
        using var request=new HttpRequestMessage(HttpMethod.Post,url);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode) throw new HttpRequestException("HTTP "+(int)response.StatusCode);
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        using var doc=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
        if(provider=="Cloudflare Workers AI")
        {
            var result=doc.RootElement.GetProperty("result");
            if(result.TryGetProperty("response",out var responseValue))
            {
                if(responseValue.ValueKind==JsonValueKind.String) return responseValue.GetString()??"";
                if(responseValue.ValueKind==JsonValueKind.Object) return responseValue.GetRawText();
                if(responseValue.ValueKind==JsonValueKind.Array) return responseValue.GetRawText();
            }
            if(result.TryGetProperty("title_ar",out _)) return result.GetRawText();
            if(result.TryGetProperty("choices",out var choices)) return choices[0].GetProperty("message").GetProperty("content").GetString()??"";
            throw new FormatException("Unexpected Workers AI response");
        }
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()??"";
    }
}
