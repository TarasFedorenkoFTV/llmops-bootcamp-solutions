// РЕФЕРЕНС W1 (для ментора). Реалізовано лише перший тиждень:
//   prompt registry — читаємо активний промпт із БД, логуємо його версію,
//   віддаємо список у /prompts, вміємо promote/rollback.
// Решта (routing, cost, cache, tools, fallback, guardrails) лишається TODO — як у стартері.

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

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): поки нічого. TODO(student, W4)

    // routing (W2): поки одна модель. TODO(student, W2)
    var model = defaultModel;

    // [W1] промпт беремо з реєстру — активну версію, а не хардкод
    var (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);

    // cache (W3): TODO(student, W3)

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
    var rawJson = await response.Content.ReadAsStringAsync();

    var answer = "";
    string? toolCall = null;
    int promptTokens = 0, completionTokens = 0;
    try
    {
        using var doc = JsonDocument.Parse(rawJson);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        answer = message.GetProperty("content").GetString() ?? "";
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            toolCall = tools[0].GetProperty("function").GetProperty("name").GetString();
        }
        var usage = doc.RootElement.GetProperty("usage");
        promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        completionTokens = usage.GetProperty("completion_tokens").GetInt32();
    }
    catch { answer = "Сервіс тимчасово недоступний."; }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // cost (W2): TODO(student, W2)
    decimal? costUsd = null;

    // [W1] лог із версією промпта
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, (int)response.StatusCode);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// [W1] список версій промпта для консолі: [ { name, version, active } ]
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
    catch { /* порожньо, якщо БД ще не готова */ }
    return Results.Json(list);
});

// [W1] promote / rollback: робимо активною задану версію support-system
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

// решта — стуби, як у стартері
app.MapGet("/observability", () => Results.Json(new { todo = "W5" }));
app.MapGet("/cost", () => Results.Json(new { todo = "W2/W5" }));
app.MapGet("/providers", () => Results.Json(new { todo = "W7" }));
app.MapGet("/approvals", () => Results.Json(new { todo = "W4" }));

app.Run("http://0.0.0.0:8080");

// [W1] активний промпт із реєстру; якщо реєстр порожній — розумний дефолт
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

record ChatIn(string Message);
