using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Configurações do GitHub e Evolution API Local
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "SEU_GITHUB_TOKEN_AQUI";
var repoOwner = "andersonharlen";
var repoName = "flight-price-monitor";
var path = "voos.json";

var evolutionApiUrl = "http://localhost:8080";
var evolutionApiKey = "B2AC8C01-9A1F-4EE0-9D38-C65B3938EC9A";
var instanceName = "voos";

Console.WriteLine("🚀 Worker Local de Monitoramento e Notificações Iniciado!");

using var httpClient = new HttpClient();

while (true)
{
    try
    {
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Verificando pendências no GitHub...");

        var (voos, sha) = await ObterVoosDoGitHubAsync(httpClient, repoOwner, repoName, path, githubToken);

        bool houveAlteracao = false;

        if (voos.Count > 0)
        {
            for (int i = 0; i < voos.Count; i++)
            {
                var voo = voos[i];

                // 1. Processa Confirmação de Cadastro Pendente
                if (voo.PendenteEnvio)
                {
                    Console.WriteLine($"[PENDÊNCIA DETECTADA] Enviando confirmação de cadastro para {voo.Telefone} ({voo.Trecho})...");

                    var mensagem = $"✅ *Alerta Cadastrado com Sucesso!*\n\n" +
                                   $"✈️ *Trecho:* {voo.Trecho}\n" +
                                   $"💰 *Preço Teto:* R$ {voo.PrecoMaximo}\n\n" +
                                   $"Você receberá notificações sempre que encontrarmos passagens abaixo deste valor.";

                    bool enviado = await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, voo.Telefone, mensagem);

                    if (enviado)
                    {
                        // Marca pendência como resolvida
                        voos[i] = voo with { PendenteEnvio = false };
                        houveAlteracao = true;
                    }
                }

                // 2. [AQUI ENTRA O SCRAPER / API DE VOOS]
                // decimal precoEncontrado = await ConsultarPrecoVooAsync(voo.Trecho);
                // if (precoEncontrado > 0 && precoEncontrado <= voo.PrecoMaximo) {
                //     await EnviarWhatsAppAsync(httpClient, evolutionApiUrl, instanceName, evolutionApiKey, voo.Telefone, $"🚨 *OFERTA!* Voo {voo.Trecho} por R$ {precoEncontrado}");
                // }
            }

            // Se alterou algum status de PendenteEnvio, salva a atualização no GitHub
            if (houveAlteracao)
            {
                var novoJson = JsonSerializer.Serialize(voos, new JsonSerializerOptions { WriteIndented = true });
                await SalvarVoosNoGitHubAsync(httpClient, repoOwner, repoName, path, githubToken, novoJson, sha);
                Console.WriteLine("✅ GitHub atualizado: Pendência marcada como concluída.");
            }
        }
        else
        {
            Console.WriteLine("Nenhum voo cadastrado no arquivo.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO NO WORKER]: {ex.Message}");
    }

    // Intervalo entre verificações (30 segundos)
    await Task.Delay(TimeSpan.FromSeconds(30));
}

// --- MÉTODOS DE INTEGRAÇÃO ---

static async Task<(List<VooConfig> Voos, string Sha)> ObterVoosDoGitHubAsync(
    HttpClient client, string owner, string repo, string path, string token)
{
    var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
    
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FlightPriceWorker", "1.0"));
    if (!string.IsNullOrEmpty(token))
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var response = await client.SendAsync(request);
    if (!response.IsSuccessStatusCode) return (new List<VooConfig>(), "");

    var body = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;

    var sha = root.GetProperty("sha").GetString() ?? "";
    var contentBase64 = root.GetProperty("content").GetString() ?? "";
    
    var jsonText = Encoding.UTF8.GetString(Convert.FromBase64String(contentBase64.Replace("\n", "")));
    var voos = JsonSerializer.Deserialize<List<VooConfig>>(jsonText) ?? new();

    return (voos, sha);
}

static async Task SalvarVoosNoGitHubAsync(
    HttpClient client, string owner, string repo, string path, string token, string conteudoJson, string sha)
{
    var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";

    using var request = new HttpRequestMessage(HttpMethod.Put, url);
    request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FlightPriceWorker", "1.0"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var bytes = Encoding.UTF8.GetBytes(conteudoJson);
    var base64Content = Convert.ToBase64String(bytes);

    var payload = new Dictionary<string, object>
    {
        { "message", "Worker Local: Atualizado PendenteEnvio para false [skip ci]" },
        { "content", base64Content },
        { "sha", sha }
    };

    request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    var response = await client.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
        var err = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[ERRO AO SALVAR NO GITHUB]: {err}");
    }
}

static async Task<bool> EnviarWhatsAppAsync(
    HttpClient client, string baseUrl, string instance, string apiKey, string telefone, string mensagem)
{
    try
    {
        var url = $"{baseUrl.TrimEnd('/')}/message/sendText/{instance}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("apikey", apiKey);

        var payload = new
        {
            number = telefone,
            text = mensagem
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);

        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[WHATSAPP ENVIADO SUCESSO]: Mensagem entregue para {telefone}");
            return true;
        }

        var err = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[ERRO EVOLUTION API]: HTTP {response.StatusCode} - {err}");
        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FALHA DE CONEXÃO EVOLUTION API LOCAL]: {ex.Message}");
        return false;
    }
}

public record VooConfig(
    [property: JsonPropertyName("Trecho")] string Trecho, 
    [property: JsonPropertyName("PrecoMaximo")] decimal PrecoMaximo, 
    [property: JsonPropertyName("Telefone")] string Telefone,
    [property: JsonPropertyName("PendenteEnvio")] bool PendenteEnvio
);
