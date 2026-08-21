using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Configurações
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "SEU_GITHUB_TOKEN";
var repoOwner = "andersonharlen";
var repoName = "flight-price-monitor";
var path = "voos.json";

var evolutionApiUrl = Environment.GetEnvironmentVariable("EVOLUTION_API_URL") 
                      ?? "https://aggregate-tricks-soup-foundation.trycloudflare.com";
var evolutionApiKey = "B2AC8C01-9A1F-4EE0-9D38-C65B3938EC9A";
var instanceName = "voos";

using var httpClient = new HttpClient();

// --- 1. ROTA DO WEBHOOK (Recebe WhatsApp) ---
app.MapPost("/webhook", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    
    // Se receber o comando BUSCAR no WhatsApp, dispara a varredura na hora
    if (body.Contains("BUSCAR", StringComparison.OrdinalIgnoreCase))
    {
        _ = Task.Run(() => ExecutarVarreduraDePrecosAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey));
    }

    return Results.Ok(new { status = "success" });
});

// --- 2. WORKER EM SEGUNDO PLANO (Agendamento Automático + Fila GitHub) ---
_ = Task.Run(async () =>
{
    // Timer para rodar a busca automática a cada 1 hora (ajuste como preferir)
    var timerBusca = new PeriodicTimer(TimeSpan.FromHours(1));

    while (true)
    {
        try
        {
            // Processa confirmações de cadastro pendentes no GitHub
            await ProcessarPendenciasGitHubAsync(httpClient, repoOwner, repoName, path, githubToken, evolutionApiUrl, instanceName, evolutionApiKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO WORKER]: {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(30));
    }
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// --- MÉTODO DE VARREDURA E ENVIO DE LINKS ---
static async Task ExecutarVarreduraDePrecosAsync(HttpClient client, string owner, string repo, string path, string token, string apiUrl, string instance, string apiKey)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 Executando varredura de preços...");

    var (voos, _) = await ObterVoosDoGitHubAsync(client, owner, repo, path, token);

    foreach (var voo in voos)
    {
        // 1. AQUI ENTRA A SUA CONSULTA NA API DE VOOS OU SCRAPER
        // Exemplo simulado de resultado encontrado:
        string[] partes = voo.Trecho.Split('-');
        string origem = partes[0];
        string destino = partes[1];
        decimal precoEncontrado = 680.00m; // Exemplo de preço vindo da API
        DateTime dataVoo = DateTime.Now.AddDays(30);

        // 2. Se o preço for menor ou igual ao teto cadastrado, envia o alerta com LINK
        if (precoEncontrado <= voo.PrecoMaximo)
        {
            string urlGoogleFlights = $"https://www.google.com/travel/flights?q=Voos+de+{origem}+para+{destino}+em+{dataVoo:yyyy-MM-dd}";

            string mensagem = $"""
            🚨 *OFERTA DE PASSAGEM ENCONTRADA!* 🚨

            ✈️ *Trecho:* {origem} ➔ {destino}
            📅 *Data:* {dataVoo:dd/MM/yyyy}

            💰 *Preço Encontrado:* R$ {precoEncontrado:N2}
            🎯 *Seu Teto:* R$ {voo.PrecoMaximo:N2}

            🔗 *Clique para ver e comprar:*
            {urlGoogleFlights}
            """;

            await EnviarWhatsAppAsync(client, apiUrl, instance, apiKey, voo.Telefone, mensagem);
        }
    }
}

// Métodos auxiliares de integração com GitHub e Evolution API mantidos abaixo...
