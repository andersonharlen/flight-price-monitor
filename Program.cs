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

// --- WEBHOOK: RECEBE E PROCESSA MENSAGENS DO WHATSAPP ---
app.MapPost("/webhook", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Trata a variação da Evolution API (data como Array ou Objeto)
        JsonElement itemData = root;

        if (root.TryGetProperty("data", out var dataElem))
        {
            if (dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
            {
                itemData = dataElem[0];
            }
            else if (dataElem.ValueKind == JsonValueKind.Object)
            {
                itemData = dataElem;
            }
        }

        // Garante que o elemento possui a chave 'key'
        if (itemData.ValueKind == JsonValueKind.Object && itemData.TryGetProperty("key", out var keyElem) && keyElem.ValueKind == JsonValueKind.Object)
        {
            bool fromMe = keyElem.TryGetProperty("fromMe", out var fm) && fm.GetBoolean();
            if (fromMe) return Results.Ok(new { status = "ignored_self" });

            string remoteJid = keyElem.TryGetProperty("remoteJid", out var rj) ? (rj.GetString() ?? "") : "";
            string telefone = remoteJid.Split('@')[0];

            // Extrai o texto da mensagem com segurança
            string textoMensagem = "";
            if (itemData.TryGetProperty("message", out var msgElem) && msgElem.ValueKind == JsonValueKind.Object)
            {
                if (msgElem.TryGetProperty("conversation", out var conv)) 
                    textoMensagem = conv.GetString() ?? "";
                else if (msgElem.TryGetProperty("extendedTextMessage", out var ext) && ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("text", out var txt)) 
                    textoMensagem = txt.GetString() ?? "";
            }

            textoMensagem = textoMensagem.Trim();
            Console.WriteLine($"[WHATSAPP RECEBIDO] De: {telefone} | Texto: '{textoMensagem}'");

            // 1. Comando CADASTRAR (ex: CADASTRAR MGF-AJU 800)
            if (textoMensagem.StartsWith("CADASTRAR", StringComparison.OrdinalIgnoreCase))
            {
                var partes = textoMensagem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (partes.Length >= 3 && decimal.TryParse(partes[2], out decimal precoTeto))
                {
                    string trecho = partes[1].ToUpper();
                    await CadastrarVooNoGitHubAsync(httpClient, repoOwner, repoName, path, githubToken, trecho, precoTeto, telefone);
                    Console.WriteLine($"[SUCESSO] Voo {trecho} cadastrado no GitHub para {telefone}");
                }
            }
            // 2. Comando BUSCAR
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

// --- WORKER LOCAL EM SEGUNDO PLANO ---
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await ProcessarPendenciasGitHubAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO WORKER]: {ex.Message}");
        }
        await Task.Delay(TimeSpan.FromSeconds(20));
    }
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// --- LÓGICA DE CADASTRO E ENVIO ---
static async Task CadastrarVooNoGitHubAsync(HttpClient client, string owner, string repo, string path, string token, string trecho, decimal precoTeto, string telefone)
{
    var (voos, sha) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);
    voos.RemoveAll(v => v.Trecho == trecho && v.Telefone == telefone);
    voos.Add(new VooConfig(trecho, precoTeto, telefone, true));

    var novoJson = JsonSerializer.Serialize(voos, new JsonSerializerOptions { WriteIndented = true });
    await SalvarVoosNoGitHubAsync(client, owner, repo, path, token, novoJson, sha, $"Cadastrado {trecho}");
}

static async Task ProcessarPendenciasGitHubAsync(HttpClient client, string owner, string repo, string path, string token, string apiUrl, string instance, string apiKey)
{
    var (voos, sha) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);
    bool alterou = false;

    for (int i = 0; i < voos.Count; i++)
    {
        if (voos[i].PendenteEnvio)
        {
            var voo = voos[i];
            string msg = $"✅ *Alerta Cadastrado com Sucesso!*\n\n✈️ *Trecho:* {voo.Trecho}\n💰 *Preço Teto:* R$ {voo.PrecoMaximo}\n\nVocê receberá atualizações assim que encontrarmos passagens abaixo deste valor.";
            
            bool enviado = await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, voo.Telefone, msg);
            if (enviado)
            {
                voos[i] = voo with { PendenteEnvio = false };
                alterou = true;
            }
        }
    }

    if (alterou)
    {
        var novoJson = JsonSerializer.Serialize(voos, new JsonSerializerOptions { WriteIndented = true });
        await SalvarVoosNoGitHubAsync(client, owner, repo, path, token, novoJson, sha, "Pendencias confirmadas");
    }
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
        var partes = voo.Trecho.Split('-');
        string orig = partes[0], dest = partes[1];
        decimal precoSimulado = 680.00m;
        DateTime dataVoo = DateTime.Now.AddDays(30);

        string urlGoogle = $"https://www.google.com/travel/flights?q=Voos+de+{orig}+para+{dest}+em+{dataVoo:yyyy-MM-dd}";
        string msg = $"🚨 *OFERTA ENCONTRADA!* 🚨\n\n✈️ *Trecho:* {orig} ➔ {dest}\n📅 *Data:* {dataVoo:dd/MM/yyyy}\n💰 *Preço Encontrado:* R$ {precoSimulado:N2}\n🎯 *Seu Teto:* R$ {voo.PrecoMaximo:N2}\n\n🔗 *Confira aqui:* {urlGoogle}";

        await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, voo.Telefone, msg);
    }
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

static async Task SalvarVoosNoGitHubAsync(HttpClient client, string owner, string repo, string path, string token, string json, string sha, string msg)
{
    var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
    using var req = new HttpRequestMessage(HttpMethod.Put, url);
    req.Headers.UserAgent.Add(new ProductInfoHeaderValue("FlightMonitor", "1.0"));
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var payload = new { message = msg, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), sha = sha };
    req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    await client.SendAsync(req);
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

public record VooConfig([property: JsonPropertyName("Trecho")] string Trecho, [property: JsonPropertyName("PrecoMaximo")] decimal PrecoMaximo, [property: JsonPropertyName("Telefone")] string Telefone, [property: JsonPropertyName("PendenteEnvio")] bool PendenteEnvio);
