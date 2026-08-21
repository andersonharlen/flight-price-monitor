using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Rota para o Keep-Alive do UptimeRobot não dar erro 404
app.MapGet("/", () => Results.Ok("Monitor de Voos Online!"));

// Configurações Globais
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "SEU_GITHUB_TOKEN";
var repoOwner = "andersonharlen";
var repoName = "flight-price-monitor";
var path = "voos.json";

var evolutionApiUrl = Environment.GetEnvironmentVariable("EVOLUTION_API_URL") 
                      ?? "https://aggregate-tricks-soup-foundation.trycloudflare.com";
var evolutionApiKey = "B2AC8C01-9A1F-4EE0-9D38-C65B3938EC9A";
var instanceName = "voos";

using var httpClient = new HttpClient();

// --- WEBHOOK MODO "RAIO-X" ---
app.MapPost("/webhook", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    
    if (string.IsNullOrWhiteSpace(body)) return Results.Ok();

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string eventType = "";
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("event", out var ev))
            eventType = ev.GetString() ?? "";

        if (!string.IsNullOrEmpty(eventType) && eventType != "messages.upsert") 
            return Results.Ok();

        Console.WriteLine("\n==========================================");
        Console.WriteLine("[📩 NOVA MENSAGEM] Identificada, analisando...");

        var targetNodeOpt = EncontrarNoComKeyMessage(root);
        if (targetNodeOpt == null) return Results.Ok();

        var node = targetNodeOpt.Value;
        var keyElem = node.GetProperty("key");

        bool fromMe = keyElem.TryGetProperty("fromMe", out var fm) && fm.GetBoolean();
        string remoteJid = keyElem.TryGetProperty("remoteJid", out var rj) ? rj.GetString() ?? "" : "";
        
        if (remoteJid.EndsWith("@g.us")) return Results.Ok();

        string telefone = remoteJid.Split('@')[0].Split(':')[0];
        string textoMensagem = ExtrairTextoRobusto(node).Trim();

        Console.WriteLine($" 👤 JID: {telefone} | Texto: '{textoMensagem}'");

        if (textoMensagem.StartsWith("CADASTRAR", StringComparison.OrdinalIgnoreCase))
        {
            var partes = textoMensagem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length >= 3)
            {
                string trecho = partes[1].ToUpper();
                string precoStr = partes[2].Replace(",", ".");
                
                if (decimal.TryParse(precoStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal precoTeto))
                {
                    bool ok = await SalvarCadastroComRetryAsync(httpClient, repoOwner, repoName, path, githubToken, trecho, precoTeto, telefone);
                    if (ok)
                    {
                        string msg = $"✅ *Alerta Cadastrado!*\n✈️ *Trecho:* {trecho}\n💰 *Teto:* R$ {precoTeto:N2}";
                        await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, msg);
                    }
                }
            }
        }
        else if (textoMensagem.Equals("BUSCAR", StringComparison.OrdinalIgnoreCase))
        {
            await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, "🔍 Buscando passagens reais no Google Flights...");
            _ = Task.Run(() => ExecutarVarreduraDePrecosAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey, telefone));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[ERRO NO WEBHOOK]: {ex.Message}");
    }

    return Results.Ok(new { status = "processed" });
});

// --- WORKER DE HORA EM HORA ---
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromHours(1));
            Console.WriteLine("[WORKER] Varredura agendada iniciada...");
            await ExecutarVarreduraGeralAlertasAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey);
        }
        catch { }
    }
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");


// ====================================================================
// MÉTODOS DE SUPORTE E BUSCA REAL (GOOGLE FLIGHTS VIA SEARCHAPI)
// ====================================================================

static async Task<decimal> BuscarPrecoGoogleFlightsAsync(HttpClient client, string origem, string destino, string dataIso)
{
    try
    {
        string apiKey = "F3j7xqnUiAPxeCUGqwHSBJAp";
        string url = $"https://www.searchapi.io/api/v1/search?engine=google_flights&departure_id={origem}&arrival_id={destino}&outbound_date={dataIso}&currency=BRL&api_key={apiKey}";

        var res = await client.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();

        // MOSTRA NO LOG O QUE A API REALMENTE RESPONDEU (Primeiros 400 caracteres)
        Console.WriteLine($"[DEBUG SEARCHAPI RESP]: {(body.Length > 400 ? body.Substring(0, 400) : body)}");

        if (!res.IsSuccessStatusCode)
        {
            Console.WriteLine($"[SearchApi Erro] Status: {res.StatusCode}");
            return 999999m;
        }

        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("best_flights", out var bestFlights) && bestFlights.GetArrayLength() > 0)
        {
            var primeiroVoo = bestFlights[0];
            if (primeiroVoo.TryGetProperty("price", out var priceElement))
            {
                if (priceElement.ValueKind == JsonValueKind.Number)
                    return priceElement.GetDecimal();
                
                string priceStr = priceElement.ToString().Replace("R$", "").Replace(".", "").Replace(",", ".").Trim();
                if (decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal preco))
                    return preco;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SearchApi Exceção]: {ex.Message}");
    }

    return 999999m;
}

static async Task ExecutarVarreduraDePrecosAsync(HttpClient client, string owner, string repo, string path, string token, string apiUrl, string instance, string apiKeyEvolution, string telefoneDestino)
{
    var (voos, _) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);
    var meusVoos = voos.Where(v => v.Telefone == telefoneDestino).ToList();

    if (!meusVoos.Any())
    {
        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKeyEvolution, telefoneDestino, "⚠️ Você não possui alertas cadastrados.");
        return;
    }

    int encontrados = 0;
    foreach (var voo in meusVoos)
    {
        if (await ProcessarEEnviarAlertaVooAsync(client, apiUrl, instance, apiKeyEvolution, voo)) encontrados++;
    }

    if (encontrados == 0)
    {
        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKeyEvolution, telefoneDestino, "📉 Busca finalizada. Nenhum voo abaixo do seu teto no momento.");
    }
}

static async Task ExecutarVarreduraGeralAlertasAsync(HttpClient client, string owner, string repo, string path, string token, string apiUrl, string instance, string apiKeyEvolution)
{
    var (voos, _) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);
    foreach (var voo in voos) await ProcessarEEnviarAlertaVooAsync(client, apiUrl, instance, apiKeyEvolution, voo);
}

static JsonElement? EncontrarNoComKeyMessage(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("key", out var key) && key.TryGetProperty("remoteJid", out _)) return element;
        foreach (var prop in element.EnumerateObject())
        {
            var found = EncontrarNoComKeyMessage(prop.Value);
            if (found != null) return found;
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            var found = EncontrarNoComKeyMessage(item);
            if (found != null) return found;
        }
    }
    return null;
}

static string ExtrairTextoRobusto(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (element.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
        if (element.TryGetProperty("caption", out var cap) && cap.ValueKind == JsonValueKind.String) return cap.GetString() ?? "";

        foreach (var prop in element.EnumerateObject())
        {
            var res = ExtrairTextoRobusto(prop.Value);
            if (!string.IsNullOrWhiteSpace(res)) return res;
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            var res = ExtrairTextoRobusto(item);
            if (!string.IsNullOrWhiteSpace(res)) return res;
        }
    }
    return "";
}

static async Task<bool> SalvarCadastroComRetryAsync(HttpClient client, string owner, string repo, string path, string token, string trecho, decimal precoTeto, string telefone)
{
    for (int tentativa = 1; tentativa <= 3; tentativa++)
    {
        var (voos, sha) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);
        voos.RemoveAll(v => v.Trecho == trecho && v.Telefone == telefone);
        voos.Add(new VooConfig(trecho, precoTeto, telefone));

        var novoJson = JsonSerializer.Serialize(voos, new JsonSerializerOptions { WriteIndented = true });
        bool salvou = await SalvarVoosNoGitHubAsync(client, owner, repo, path, token, novoJson, sha, $"Cadastrado {trecho}");
        
        if (salvou) return true;
        await Task.Delay(500);
    }
    return false;
}

static async Task<(List<VooConfig> Voos, string Sha)> ObterVoosDoGitHubAsync(HttpClient client, string owner, string repo, string path, string token)
{
    var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.UserAgent.Add(new ProductInfoHeaderValue("FlightMonitor", "1.0"));
    if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var res = await client.SendAsync(req);
    if (!res.IsSuccessStatusCode) return (new List<VooConfig>(), "");

    var body = await res.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    var sha = doc.RootElement.GetProperty("sha").GetString() ?? "";
    var contentBase64 = doc.RootElement.GetProperty("content").GetString() ?? "";
    var jsonText = Encoding.UTF8.GetString(Convert.FromBase64String(contentBase64.Replace("\n", "")));

    return (JsonSerializer.Deserialize<List<VooConfig>>(jsonText) ?? new(), sha);
}

static async Task<bool> SalvarVoosNoGitHubAsync(HttpClient client, string owner, string repo, string path, string token, string json, string sha, string msg)
{
    try
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("FlightMonitor", "1.0"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payload = new { message = msg, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), sha = sha };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        
        var res = await client.SendAsync(req);
        return res.IsSuccessStatusCode;
    }
    catch { return false; }
}

static async Task<bool> EnviarWhatsAppAsync(HttpClient client, string baseUrl, string instance, string apiKey, string telefone, string mensagem)
{
    try
    {
        var url = $"{baseUrl.TrimEnd('/')}/message/sendText/{instance}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("apikey", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(new { number = telefone, text = mensagem }), Encoding.UTF8, "application/json");
        
        var res = await client.SendAsync(req);
        return res.IsSuccessStatusCode;
    }
    catch { return false; }
}

public record VooConfig([property: JsonPropertyName("Trecho")] string Trecho, [property: JsonPropertyName("PrecoMaximo")] decimal PrecoMaximo, [property: JsonPropertyName("Telefone")] string Telefone);
