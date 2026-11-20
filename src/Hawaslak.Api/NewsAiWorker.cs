using System.Threading;

sealed class NewsAiWorker : BackgroundService
{
    private readonly NewsStore _store;
    private readonly NewsAiService _ai;
    private readonly ILogger<NewsAiWorker> _logger;
    private readonly SemaphoreSlim _gate = new(1,1);
    private DateOnly _date = DateOnly.FromDateTime(DateTime.UtcNow);
    private int _used;
    private DateTime _cooldownUntil = DateTime.MinValue;
    public NewsAiWorker(NewsStore store, NewsAiService ai, ILogger<NewsAiWorker> logger)
    {
        _store=store;_ai=ai;_logger=logger;
    }
    public async Task<object> ProcessBatch(int requested,CancellationToken ct)
    {
        if (!_store.Configured) return new {processed=0,error="Database is not configured"};
        if (!_ai.Providers().Any(p=>p.Configured)) return new {processed=0,error="No AI providers configured"};
        if (!await _gate.WaitAsync(0,ct)) return new {processed=0,error="AI batch is already running"};
        try
        {
            var now=DateTime.UtcNow;
            var today=DateOnly.FromDateTime(now);
            if(_date!=today){_date=today;_used=0;}
            var maxDaily=int.TryParse(Environment.GetEnvironmentVariable("AI_DAILY_REQUEST_LIMIT"),out var limit)?Math.Clamp(limit,1,500):100;
            var allowed=Math.Min(Math.Clamp(requested,1,20),Math.Max(0,maxDaily-_used));
            if (allowed==0) return new {processed=0,error="Daily processing limit reached"};
            if(now<_cooldownUntil) return new {processed=0,error="All providers are cooling down"};
            var items=await _store.GetPendingAiBatch(allowed,ct);
            var completed=0;
            if(items.Count==0) return new {processed=0,published=await PublishIfEnabled(ct),error="No pending articles eligible for AI preparation"};
            foreach (var item in items)
            {
                _used++;
                try
                {
                    var draft=await _ai.Generate(new AiRequest(item.Title,item.Context,item.Source,item.Country),ct);
                    await _store.SaveAiDraft(item.Id,draft,ct);
                    completed++;
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch(Exception ex)
                {
                    _logger.LogWarning("AI processing suspended after error: {Message}",ex.Message);
                    _cooldownUntil=DateTime.UtcNow.AddMinutes(5);
                    return new {processed=completed,error="AI provider failed; retry after cooldown. Check Render logs for the upstream error"};
                }
            }
            return new {processed=completed,published=await PublishIfEnabled(ct),error=(string?)null};
        }
        finally{_gate.Release();}
    }
    private async Task<int> PublishIfEnabled(CancellationToken ct) =>
        Environment.GetEnvironmentVariable("AI_AUTO_PUBLISH")?.Equals("true",StringComparison.OrdinalIgnoreCase)==true
        ? await _store.AutoPublishApproved(ct) : 0;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try{await Task.Delay(TimeSpan.FromSeconds(45),ct);}catch(OperationCanceledException){return;}
        while(!ct.IsCancellationRequested)
        {
            if(Environment.GetEnvironmentVariable("AI_AUTO_PREPARE")?.Equals("true",StringComparison.OrdinalIgnoreCase)==true)
            {
                try{await ProcessBatch(10,ct);}
                catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
                catch(Exception ex){_logger.LogWarning("AI batch encountered a temporary failure: {Message}",ex.Message);}
            }
            try{await Task.Delay(TimeSpan.FromMinutes(5),ct);}catch(OperationCanceledException){break;}
        }
    }
}
