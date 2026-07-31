// РЕФЕРЕНС (кумулятивно): W1 prompt registry, W2 routing+cost, W3 cache+tools,
// W4 fallback + graceful degradation, HITL-approval, guardrails (маскування PII).
// Кеш/лічильники/черга approvals — in-memory (для еталона досить; у проді — Redis/БД).

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";

var prices = new Dictionary<string, (decimal In, decimal Out)>
{
    ["mock-mini"] = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m, 0.01m),
    ["gpt-4o-mini"] = (0.00015m, 0.0006m),
    ["gpt-4o"] = (0.0025m, 0.01m),
};

var cache = new ConcurrentDictionary<string, string>();
var stats = new Stats();
// [W4] черга підтверджень (HITL): id -> (дія, результат). result == null => очікує
var approvals = new ConcurrentDictionary<string, Approval>();
// [W4] circuit breaker: N збоїв поспіль -> модель "відкрита" на cooldown
var breakers = new ConcurrentDictionary<string, Breaker>();

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // [W4] guardrails: маскуємо email перед відправкою в модель
    var userMessage = MaskPii(body.Message);

    // [W2] routing
    var model = Route(body.Message, defaultModel);

    // [W1] активний промпт
    var (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);

    var cacheKey = $"{model}|{systemPrompt}|{userMessage}";
    var answer = "";
    string? toolCall = null;
    int promptTokens = 0, completionTokens = 0, status = 200;

    if (cache.TryGetValue(cacheKey, out var cached))
    {
        answer = cached;
        Interlocked.Increment(ref stats.CacheHits);
    }
    else
    {
        Interlocked.Increment(ref stats.CacheMisses);

        // [W4] fallback: пробуємо по черзі; на mock другий елемент — та сама модель,
        // тож на реальний провайдер треба міняти список (напр. [model, "azure-gpt-4o"]).
        var chain = defaultModel == "mock" ? new[] { model, "mock" } : new[] { model, "azure-gpt-4o" };
        var http = httpFactory.CreateClient();
        var ok = false;
        for (int i = 0; i < chain.Length && !ok; i++)
        {
            var br = breakers.GetOrAdd(chain[i], _ => new Breaker());
            if (br.OpenUntil > DateTimeOffset.UtcNow) continue;  // [W4] circuit open -> пропускаємо модель
            if (i > 0) Interlocked.Increment(ref stats.Fallbacks);
            var res = await CallGateway(http, gateway, chain[i], systemPrompt, userMessage);
            status = res.status;
            if (res.ok)
            {
                br.Fails = 0; br.OpenUntil = default;
                ok = true;
                model = chain[i];
                answer = res.answer;
                toolCall = res.tool;
                promptTokens = res.pt;
                completionTokens = res.ct;
            }
            else if (++br.Fails >= 3)
            {
                br.OpenUntil = DateTimeOffset.UtcNow.AddSeconds(30);  // [W4] відкриваємо на 30с
            }
        }

        if (!ok)
        {
            // graceful degradation — усі спроби невдалі
            answer = "Вибачте, тимчасові проблеми на нашому боці. Спробуйте, будь ласка, трохи згодом.";
            status = status == 200 ? 503 : status;
        }
        else if (toolCall != null)
        {
            // [W3/W4] інструменти: read-only виконуємо одразу; незворотну дію — через approval
            if (toolCall == "create_ticket")
            {
                var id = Guid.NewGuid().ToString("N")[..6];
                approvals[id] = new Approval(toolCall, null);
                answer += $" (очікує підтвердження оператора, id={id})";
            }
            else
            {
                var result = RunTool(toolCall);
                if (result != null) answer += $" ({result})";
            }
        }

        // кешуємо лише «чисті» відповіді без інструментів
        if (ok && status == 200 && toolCall == null) cache[cacheKey] = answer;
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    decimal? costUsd = prices.TryGetValue(model, out var pr)
        ? Math.Round(promptTokens / 1000m * pr.In + completionTokens / 1000m * pr.Out, 6)
        : null;

    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/prompts", async () =>
{
    var list = new List<object>();
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT name, version, active FROM prompts ORDER BY created_at", db);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new { name = r.GetString(0), version = r.GetString(1), active = r.GetBoolean(2) });
    }
    catch { }
    return Results.Json(list);
});

app.MapPost("/prompts/{version}/activate", async (string version) =>
{
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE prompts SET active = (version = @v) WHERE name = 'support-system'", db);
        cmd.Parameters.AddWithValue("v", version);
        await cmd.ExecuteNonQueryAsync();
    }
    catch { }
    return Results.Ok(new { activated = version });
});

app.MapGet("/cost", async () =>
{
    decimal today = 0;
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(cost_usd), 0) FROM requests WHERE created_at::date = CURRENT_DATE", db);
        today = (await cmd.ExecuteScalarAsync()) is decimal d ? d : 0;
    }
    catch { }
    return Results.Json(new { today_usd = Math.Round(today, 4), budget_usd = 5.0 });
});

// [W4] черга HITL: pending — ті, що очікують
app.MapGet("/approvals", () =>
{
    var pending = approvals
        .Where(kv => kv.Value.Result == null)
        .Select(kv => new { id = kv.Key, action = kv.Value.Action })
        .ToList();
    return Results.Json(new { pending });
});

// [W4] підтвердити дію -> виконати інструмент
app.MapPost("/approvals/{id}/approve", (string id) =>
{
    if (approvals.TryGetValue(id, out var a) && a.Result == null)
    {
        var result = RunTool(a.Action) ?? "виконано";
        approvals[id] = a with { Result = result };
        return Results.Ok(new { id, result });
    }
    return Results.NotFound(new { id, error = "not found or already done" });
});

// [W5] агрегати за сьогодні: requests / p95 latency / error-rate з БД,
// cache-hit і fallback — з in-memory лічильників. Консоль показує ці плитки.
app.MapGet("/observability", async () =>
{
    long requests = 0; double p95 = 0, errorRate = 0;
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*)::int8, "
            + "COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms), 0)::float8, "
            + "COALESCE(AVG(CASE WHEN status <> '200' THEN 1.0 ELSE 0 END), 0)::float8 * 100 "
            + "FROM requests WHERE created_at::date = CURRENT_DATE", db);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync()) { requests = r.GetInt64(0); p95 = r.GetDouble(1); errorRate = r.GetDouble(2); }
    }
    catch { }
    int total = stats.CacheHits + stats.CacheMisses;
    return Results.Json(new
    {
        p95_ms = (int)p95,
        requests,
        cache_hit_pct = total == 0 ? 0 : Math.Round(stats.CacheHits * 100.0 / total, 1),
        error_rate_pct = Math.Round(errorRate, 1),
        fallback_events = stats.Fallbacks,
    });
});

// [W5/W7] здоров'я провайдерів (на mock — завжди ok)
app.MapGet("/providers", () => Results.Json(new
{
    providers = new[] { new { name = "mock", status = "ok" } }
}));

app.Run("http://0.0.0.0:8080");

// один виклик моделі через gateway; ok=false, якщо мережевий збій або статус >= 400
static async Task<(bool ok, string answer, string? tool, int pt, int ct, int status)> CallGateway(
    HttpClient http, string gateway, string model, string system, string user)
{
    try
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });
        var resp = await http.PostAsync($"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var status = (int)resp.StatusCode;
        if (status >= 400) return (false, "", null, 0, 0, status);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var answer = message.GetProperty("content").GetString() ?? "";
        string? tool = null;
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            tool = tools[0].GetProperty("function").GetProperty("name").GetString();
        var usage = doc.RootElement.GetProperty("usage");
        return (true, answer, tool, usage.GetProperty("prompt_tokens").GetInt32(),
            usage.GetProperty("completion_tokens").GetInt32(), status);
    }
    catch
    {
        return (false, "", null, 0, 0, 0);  // мережевий збій / gateway лежить
    }
}

static async Task<(string version, string body)> GetActivePrompt(string conn)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT version, body FROM prompts WHERE active = true ORDER BY created_at DESC LIMIT 1", db);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync()) return (r.GetString(0), r.GetString(1));
    }
    catch { }
    return ("none", "You are a support assistant.");
}

static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status) "
            + "VALUES (@id, @model, @pv, @lat, @pt, @ct, @cost, @status)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("pv", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
        await cmd.ExecuteNonQueryAsync();
    }
    catch { }
}

static string Route(string message, string def)
{
    if (def != "mock") return def;
    var u = message.ToLowerInvariant();
    bool escalation = u.Contains("поверн") || u.Contains("терміново") || u.Contains("refund") || u.Contains("скарг");
    return escalation ? "mock-strong" : "mock-mini";
}

// [W4] маскуємо email (найпростіший guardrail на PII)
static string MaskPii(string s) => Regex.Replace(s, @"[\w.\-]+@[\w.\-]+", "[email]");

static string? RunTool(string name) => name switch
{
    "lookup_order" => "статус: оплачено, доставку призначено",
    "create_ticket" => "тікет #T-" + Guid.NewGuid().ToString("N")[..4],
    _ => null,
};

class Stats
{
    public int CacheHits;
    public int CacheMisses;
    public int Fallbacks;
}

record Approval(string Action, string? Result);

class Breaker { public int Fails; public DateTimeOffset OpenUntil; }

record ChatIn(string Message);
