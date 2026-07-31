// РЕФЕРЕНС (кумулятивно): W1 prompt registry, W2 routing+cost, W3 cache+tools.
// Кеш і лічильники — in-memory (для еталона досить; у проді — Redis).

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";

// [W2] прайс за 1k токенів (in, out)
var prices = new Dictionary<string, (decimal In, decimal Out)>
{
    ["mock-mini"] = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m, 0.01m),
    ["gpt-4o-mini"] = (0.00015m, 0.0006m),
    ["gpt-4o"] = (0.0025m, 0.01m),
};

// [W3] простий in-memory кеш + лічильники
var cache = new ConcurrentDictionary<string, string>();
var stats = new Stats();

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): TODO(student, W4)

    // [W2] routing
    var model = Route(body.Message, defaultModel);

    // [W1] активний промпт із реєстру
    var (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);

    // [W3] кеш: якщо вже відповідали на цей самий запит — беремо звідти, у модель не йдемо
    var cacheKey = $"{model}|{systemPrompt}|{body.Message}";
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

        // fallback (W4): TODO(student, W4)
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = body.Message }
            }
        });
        var http = httpFactory.CreateClient();
        var response = await http.PostAsync(
            $"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        status = (int)response.StatusCode;
        var rawJson = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            answer = message.GetProperty("content").GetString() ?? "";
            if (message.TryGetProperty("tool_calls", out var tools)
                && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            {
                toolCall = tools[0].GetProperty("function").GetProperty("name").GetString();
                // [W3] виконуємо інструмент і додаємо результат до відповіді
                var result = RunTool(toolCall);
                if (result != null) answer += $" ({result})";
            }
            var usage = doc.RootElement.GetProperty("usage");
            promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            completionTokens = usage.GetProperty("completion_tokens").GetInt32();
        }
        catch { answer = "Сервіс тимчасово недоступний."; }

        if (status == 200) cache[cacheKey] = answer;
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // [W2] cost
    decimal? costUsd = prices.TryGetValue(model, out var pr)
        ? Math.Round(promptTokens / 1000m * pr.In + completionTokens / 1000m * pr.Out, 6)
        : null;

    // [W1] лог із версією промпта
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// [W1] реєстр промптів
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

// [W1] promote / rollback
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

// [W2] вартість за сьогодні + бюджет
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

// решта — стуби
app.MapGet("/observability", () => Results.Json(new { todo = "W5" }));
app.MapGet("/providers", () => Results.Json(new { todo = "W7" }));
app.MapGet("/approvals", () => Results.Json(new { todo = "W4" }));

app.Run("http://0.0.0.0:8080");

// [W1] активний промпт із реєстру
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

// [W2] маршрутизація: ескалацію — на сильнішу модель
static string Route(string message, string def)
{
    if (def != "mock") return def;
    var u = message.ToLowerInvariant();
    bool escalation = u.Contains("поверн") || u.Contains("терміново") || u.Contains("refund") || u.Contains("скарг");
    return escalation ? "mock-strong" : "mock-mini";
}

// [W3] мінімальний реєстр інструментів
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

record ChatIn(string Message);
