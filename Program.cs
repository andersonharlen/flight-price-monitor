using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

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

// --- 1. WEBHOOK BLINDADO ---
app.MapPost("/webhook", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    
    if (string.IsNullOrWhiteSpace(body)) return Results.Ok();

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // 1. Busca magicamente o bloco que contém o remetente (key) em QUALQUER nível do JSON
        var targetNodeOpt = EncontrarNoComKeyMessage(root);
        if (targetNodeOpt == null) return Results.Ok(); // Não é evento de mensagem válida

        var node = targetNodeOpt.Value;
        var keyElem = node.GetProperty("key");

        // 2. Filtros: Ignorar nós mesmos e grupos
        if (keyElem.TryGetProperty("fromMe", out var fm) && fm.GetBoolean()) return Results.Ok();
        
        string remoteJid = keyElem.TryGetProperty("remoteJid", out var rj) ? rj.GetString() ?? "" : "";
        if (remoteJid.EndsWith("@g.us")) return Results.Ok();

        string telefone = remoteJid.Split('@')[0].Split(':')[0];
        if (string.IsNullOrEmpty(telefone)) return Results.Ok();

        // 3. Extrai o texto de qualquer lugar onde a API tenha escondido
        string textoMensagem = ExtrairTextoRobusto(node).Trim();
        if (string.IsNullOrEmpty(textoMensagem)) return Results.Ok();

        Console.WriteLine($"[WHATSAPP DIRETO] De: {telefone} | Recebido: '{textoMensagem}'");

        // --- COMANDOS ---
        if (textoMensagem.StartsWith("CADASTRAR", StringComparison.OrdinalIgnoreCase))
        {
            var partes = textoMensagem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length >= 3)
            {
                string trecho = partes[1].ToUpper();
                string precoStr = partes[2].Replace(",", "."); // Proteção contra vírgula
                
                if (decimal.TryParse(precoStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal precoTeto))
                {
                    Console.WriteLine($"[AÇÃO] Cadastrando {trecho} com teto R$ {precoTeto}");
                    bool ok = await SalvarCadastroComRetryAsync(httpClient, repoOwner, repoName, path, githubToken, trecho, precoTeto, telefone);
                    
                    if (ok)
                    {
                        string msgConfirmacao = $"✅ *Alerta Cadastrado com Sucesso!*\n\n✈️ *Trecho:* {trecho}\n💰 *Preço Teto:* R$ {precoTeto:N2}\n\nVocê receberá alertas assim que encontrarmos voos abaixo deste valor.";
                        await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, msgConfirmacao);
                    }
                    else
                    {
                        await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, "❌ Erro ao salvar no banco de dados do GitHub. Tente novamente.");
                    }
                }
                else
                {
                    await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, "⚠️ Preço inválido. Exemplo correto: *CADASTRAR MGF-AJU 900*");
                }
            }
        }
        else if (textoMensagem.Equals("BUSCAR", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[AÇÃO] Busca manual iniciada para {telefone}");
            await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, "🔍 Iniciando varredura das suas passagens cadastradas...");
            _ = Task.Run(() => ExecutarVarreduraDePrecosAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey, telefone));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO CRÍTICO WEBHOOK]: {ex.Message}\nPayload Recebido: {body}");
    }

    return Results.Ok(new { status = "processed" });
});

// --- 2. WORKER (VARREDURA HORA EM HORA) ---
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromHours(1));
            Console.WriteLine("[WORKER] Iniciando varredura de hora em hora...");
            await ExecutarVarreduraGeralAlertasAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO WORKER]: {ex.Message}");
        }
    }
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");


// ====================================================================
// FUNÇÕES CAÇA-NÍQUEL (BUSCA RECURSIVA NO JSON)
// ====================================================================

// Procura em todo o JSON até achar a propriedade 'key' que tenha 'remoteJid'
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

// Procura em todo o JSON pelas propriedades conhecidas de texto
static string ExtrairTextoRobusto(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (element.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
        if (element.TryGetProperty("caption", out var cap) && cap.ValueKind == JsonValueKind.String) return cap.GetString() ?? ""; // Pra foto com legenda

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

// ====================================================================
// LÓGICA DE NEGÓCIO E INTEGRAÇÕES
// ====================================================================

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

static async Task ExecutarVarreduraDePrecosAsync(HttpClient client, string owner, string repo, string path, string token, string apiUrl, string instance, string apiKey, string telefoneDestino)
{
    var (voos, _) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);
    var meusVoos = voos.Where(v => v.Telefone == telefoneDestino).ToList();

    if (!meusVoos.Any())
    {
        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, telefoneDestino, "⚠️ Você não possui nenhum trecho cadastrado no momento.");
        return;
    }

    int encontrados = 0;
    foreach (var voo in meusVoos)
    {
        bool achouPrecoBom = await ProcessarEEnviarAlertaVooAsync(client, apiUrl, instance, apiKey, voo);
        if (achouPrecoBom) encontrados++;
    }

    if (encontrados == 0)
    {
        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, telefoneDestino, "📉 Busca finalizada. No momento, não há voos abaixo dos tetos que você configurou.");
    }
}

static async Task ExecutarVarreduraGeralAlertasAsync(HttpClient client, string owner, string repo, string path, string token, string apiUrl, string instance, string apiKey)
{
    var (voos, _) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);
    foreach (var voo in voos)
    {
        await ProcessarEEnviarAlertaVooAsync(client, apiUrl, instance, apiKey, voo);
    }
}

static async Task<bool> ProcessarEEnviarAlertaVooAsync(HttpClient client, string apiUrl, string instance, string apiKey, VooConfig voo)
{
    var partes = voo.Trecho.Split('-');
    if (partes.Length < 2) return false;

    string orig = partes[0], dest = partes[1];
    
    // MOCK: Valor simulado (substituir depois pela raspagem real)
    decimal precoEncontrado = 680.00m; 
    DateTime dataVoo = DateTime.Now.AddDays(30);

    if (precoEncontrado <= voo.PrecoMaximo)
    {
        string urlGoogle = $"https://www.google.com/travel/flights?q=Voos+so+ida+de+{orig}+para+{dest}+em+{dataVoo:yyyy-MM-dd}";
        string msg = $"🚨 *OFERTA ENCONTRADA!* 🚨\n\n✈️ *Trecho:* {orig} ➔ {dest}\n📅 *Data:* {dataVoo:dd/MM/yyyy}\n💰 *Preço Encontrado:* R$ {precoEncontrado:N2}\n🎯 *Seu Teto:* R$ {voo.PrecoMaximo:N2}\n\n🔗 *Confira e compre aqui:* {urlGoogle}";

        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, voo.Telefone, msg);
        return true;
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
