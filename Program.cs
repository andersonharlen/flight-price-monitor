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

// --- 1. WEBHOOK (COM LOGS DE DIAGNÓSTICO PASSO A PASSO) ---
app.MapPost("/webhook", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body)) return Results.Ok(new { status = "empty_body" });

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
            // Ignora mensagens enviadas pelo próprio número
            bool fromMe = keyElem.TryGetProperty("fromMe", out var fm) && fm.GetBoolean();
            if (fromMe) 
            {
                Console.WriteLine("[WEBHOOK] Ignorado: enviada por mim (fromMe).");
                return Results.Ok(new { status = "ignored_self" });
            }

            // Filtro de Grupo (@g.us)
            string remoteJid = keyElem.TryGetProperty("remoteJid", out var rj) ? rj.GetString() ?? "" : "";
            if (remoteJid.EndsWith("@g.us")) 
            {
                Console.WriteLine($"[WEBHOOK] Ignorado: mensagem de grupo ({remoteJid}).");
                return Results.Ok(new { status = "ignored_group" });
            }

            string telefone = remoteJid.Split('@')[0].Split(':')[0];
            if (string.IsNullOrEmpty(telefone)) 
            {
                Console.WriteLine("[WEBHOOK] Ignorado: número de telefone não identificado.");
                return Results.Ok(new { status = "no_phone" });
            }

            // Extrai o texto da mensagem tratando estruturas simples e complexas
            string textoMensagem = "";
            if (itemData.TryGetProperty("message", out var msgElem))
            {
                textoMensagem = ExtrairTextoRobusto(msgElem);
            }

            textoMensagem = textoMensagem.Trim();
            Console.WriteLine($"[WHATSAPP RECEBIDO] De: {telefone} | Texto: '{textoMensagem}'");

            if (string.IsNullOrEmpty(textoMensagem)) 
            {
                Console.WriteLine("[WEBHOOK] Ignorado: texto extraído está vazio.");
                return Results.Ok(new { status = "ignored_empty_text" });
            }

            // COMANDO: CADASTRAR
            if (textoMensagem.StartsWith("CADASTRAR", StringComparison.OrdinalIgnoreCase))
            {
                var partes = textoMensagem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (partes.Length >= 3 && decimal.TryParse(partes[2], out decimal precoTeto))
                {
                    string trecho = partes[1].ToUpper();
                    Console.WriteLine($"[AÇÃO] Tentando cadastrar {trecho} com teto R$ {precoTeto} para {telefone}...");

                    bool ok = await SalvarCadastroComRetryAsync(httpClient, repoOwner, repoName, path, githubToken, trecho, precoTeto, telefone);
                    
                    if (ok)
                    {
                        Console.WriteLine($"[SUCESSO] Salvo no GitHub. Enviando confirmação WhatsApp...");
                        string msgConfirmacao = $"✅ *Alerta Cadastrado com Sucesso!*\n\n✈️ *Trecho:* {trecho}\n💰 *Preço Teto:* R$ {precoTeto:N2}\n\nVocê receberá atualizações automáticas assim que encontrarmos passagens abaixo deste valor.";
                        await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, msgConfirmacao);
                    }
                    else
                    {
                        Console.WriteLine($"[ERRO] Falha ao atualizar o JSON no GitHub.");
                        await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, "❌ Falha ao salvar no banco de dados. Tente novamente em instantes.");
                    }
                }
                else
                {
                    await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, telefone, "⚠️ Formato incorreto. Use: *CADASTRAR MGF-AJU 900*");
                }
            }
            // COMANDO: BUSCAR
            else if (textoMensagem.Equals("BUSCAR", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[AÇÃO] Executando busca para {telefone}...");
                _ = Task.Run(() => ExecutarVarreduraDePrecosAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey, telefone));
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO CRÍTICO NO WEBHOOK]: {ex.Message}");
    }

    return Results.Ok(new { status = "processed" });
});

// --- 2. WORKER (VARREDURA PERIÓDICA A CADA 1 HORA) ---
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

// --- EXTRATOR DE TEXTO RESISTENTE A MUDANÇAS DA API ---
static string ExtrairTextoRobusto(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? "";
    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? "";
        if (element.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() ?? "";

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.NameEquals("key") || prop.NameEquals("messageTimestamp") || prop.NameEquals("status") || prop.NameEquals("contextInfo")) 
                continue;

            var res = ExtrairTextoRobusto(prop.Value);
            if (!string.IsNullOrWhiteSpace(res)) return res;
        }
    }
    return "";
}

// --- LÓGICA DE NEGÓCIO ---
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
        Console.WriteLine($"[RETRY {tentativa}/3] Conflito de SHA no GitHub, tentando novamente...");
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
