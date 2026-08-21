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

// --- 1. WEBHOOK (RESPOSTA IMEDIATA) ---
app.MapPost("/webhook", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        JsonElement itemData = root;
        if (root.TryGetProperty("data", out var dataElem))
        {
            if (dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
                itemData = dataElem[0];
            else if (dataElem.ValueKind == JsonValueKind.Object)
                itemData = dataElem;
        }

        if (itemData.ValueKind == JsonValueKind.Object && itemData.TryGetProperty("key", out var keyElem))
        {
            bool fromMe = keyElem.TryGetProperty("fromMe", out var fm) && fm.GetBoolean();
            if (fromMe) return Results.Ok(new { status = "ignored_self" });

            // Filtra mensagens de grupo
            string remoteJid = keyElem.TryGetProperty("remoteJid", out var rj) ? rj.GetString() ?? "" : "";
            if (remoteJid.EndsWith("@g.us")) 
                return Results.Ok(new { status = "ignored_group" });

            string telefone = remoteJid.Split('@')[0].Split(':')[0];
            if (string.IsNullOrEmpty(telefone)) return Results.Ok(new { status = "no_phone" });

            string textoMensagem = "";
            if (itemData.TryGetProperty("message", out var msgElem))
            {
                textoMensagem = ExtrairTextoMensagem(msgElem);
            }

            textoMensagem = textoMensagem.Trim();
            if (string.IsNullOrEmpty(textoMensagem)) return Results.Ok(new { status = "ignored_non_text" });

            Console.WriteLine($"[WHATSAPP DIRETO] De: {telefone} | Texto: '{textoMensagem}'");

            // Comando CADASTRAR
            if (textoMensagem.StartsWith("CADASTRAR", StringComparison.OrdinalIgnoreCase))
            {
                var partes = textoMensagem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (partes.Length >= 3 && decimal.TryParse(partes[2], out decimal precoTeto))
                {
                    string trecho = partes[1].ToUpper();
                    
                    bool ok = await SalvarCadastroComRetryAsync(httpClient, repoOwner, repoName, path, githubToken, trecho, precoTeto, telefone);
                    
                    if (ok)
                    {
                        Console.WriteLine($"[SUCESSO] Voo {trecho} cadastrado no GitHub para {telefone}");
                        string msgConfirmacao = $"✅ *Alerta Cadastrado com Sucesso!*\n\n✈️ *Trecho:* {trecho}\n💰 *Preço Teto:* R$ {precoTeto:N2}\n\nVocê receberá atualizações automáticas assim que encontrarmos passagens abaixo deste valor.";
                        await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, msgConfirmacao);
                    }
                    else
                    {
                        Console.WriteLine($"[ERRO] Falha ao cadastrar no GitHub para {telefone}");
                        await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, "❌ Não foi possível salvar seu alerta. Tente novamente em alguns instantes.");
                    }
                }
            }
            // Comando BUSCAR
            else if (textoMensagem.Equals("BUSCAR", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => ExecutarVarreduraDePrecosAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey, telefone));
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO WEBHOOK]: {ex.Message}");
    }

    return Results.Ok(new { status = "processed" });
});

// --- 2. WORKER EM SEGUNDO PLANO (APENAS VARREDURA HORA EM HORA) ---
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromHours(1));
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

// --- EXTRATORES ---
static string ExtrairTextoMensagem(JsonElement msgElem)
{
    if (msgElem.ValueKind != JsonValueKind.Object) return "";

    if (msgElem.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.String)
        return c.GetString() ?? "";

    if (msgElem.TryGetProperty("extendedTextMessage", out var ext) && ext.TryGetProperty("text", out var extText))
        return extText.GetString() ?? "";

    if (msgElem.TryGetProperty("imageMessage", out var img) && img.TryGetProperty("caption", out var cap))
        return cap.GetString() ?? "";

    return "";
}

// --- LÓGICA DE NEGÓCIO ---
static async Task<bool> SalvarCadastroComRetryAsync(HttpClient client, string owner, string repo, string path, string token, string trecho, decimal precoTeto, string telefone)
{
    for (int tentativa = 0; tentativa < 3; tentativa++)
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
        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, telefoneDestino, "⚠️ Você não possui alertas cadastrados no momento.");
        return;
    }

    foreach (var voo in meusVoos)
    {
        await ProcessarEEnviarAlertaVooAsync(client, apiUrl, instance, apiKey, voo);
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

static async Task ProcessarEEnviarAlertaVooAsync(HttpClient client, string apiUrl, string instance, string apiKey, VooConfig voo)
{
    var partes = voo.Trecho.Split('-');
    if (partes.Length < 2) return;

    string orig = partes[0], dest = partes[1];
    decimal precoEncontrado = 680.00m;
    DateTime dataVoo = DateTime.Now.AddDays(30);

    if (precoEncontrado <= voo.PrecoMaximo)
    {
        string urlGoogle = $"https://www.google.com/travel/flights?q=Voos+so+ida+de+{orig}+para+{dest}+em+{dataVoo:yyyy-MM-dd}";
        string msg = $"🚨 *OFERTA ENCONTRADA!* 🚨\n\n✈️ *Trecho:* {orig} ➔ {dest}\n📅 *Data:* {dataVoo:dd/MM/yyyy}\n💰 *Preço Encontrado:* R$ {precoEncontrado:N2}\n🎯 *Seu Teto:* R$ {voo.PrecoMaximo:N2}\n\n🔗 *Confira e compre aqui:* {urlGoogle}";

        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, voo.Telefone, msg);
    }
}

// --- INTEGRAÇÃO EXTERNA ---
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
