using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");
var app = builder.Build();

var arquivoVoos = "voos.json";

app.MapGet("/", () => "API de Monitoramento de Voos Rodando!");

app.MapGet("/voos", async () =>
{
    if (!File.Exists(arquivoVoos)) return Results.Ok(new List<VooConfig>());
    var json = await File.ReadAllTextAsync(arquivoVoos);
    var voos = JsonSerializer.Deserialize<List<VooConfig>>(json) ?? new();
    return Results.Ok(voos);
});

app.MapPost("/webhook", async (HttpContext context) =>
{
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
            msg.TryGetProperty("conversation", out var textProp))
        {
            var textoMensagem = textProp.GetString()?.Trim() ?? "";
            
            if (data.TryGetProperty("key", out var keyProp) && 
                keyProp.TryGetProperty("remoteJid", out var remoteJidProp))
            {
                var remetente = remoteJidProp.GetString() ?? "";

                if (textoMensagem.StartsWith("CADASTRAR", StringComparison.OrdinalIgnoreCase))
                {
                    var partes = textoMensagem.Split(' ');
                    if (partes.Length >= 3)
                    {
                        var trecho = partes[1].ToUpper();
                        if (decimal.TryParse(partes[2], out var precoMaximo))
                        {
                            var telefone = remetente.Split('@')[0];

                            List<VooConfig> voos = new();
                            if (File.Exists(arquivoVoos))
                            {
                                var jsonExistente = await File.ReadAllTextAsync(arquivoVoos);
                                voos = JsonSerializer.Deserialize<List<VooConfig>>(jsonExistente) ?? new();
                            }

                            voos.RemoveAll(v => v.Trecho == trecho && v.Telefone == telefone);
                            voos.Add(new VooConfig(trecho, precoMaximo, telefone, true));

                            var novoJson = JsonSerializer.Serialize(voos, new JsonSerializerOptions { WriteIndented = true });
                            await File.WriteAllTextAsync(arquivoVoos, novoJson);

                            // Salva automaticamente no GitHub para persistir no repositório
                            await SalvarVoosNoGitHubAsync(novoJson);

                            Console.WriteLine($"[SUCESSO] Voo {trecho} cadastrado para {telefone} com teto R$ {precoMaximo} e salvo no GitHub!");
                        }
                    }
                }
            }
        }

        return Results.Ok(new { status = "processado" });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO WEBHOOK SILENCIADO]: {ex.Message}");
        return Results.Ok(new { status = "ignorado" });
    }
});

app.Run();

async Task SalvarVoosNoGitHubAsync(string conteudoJson)
{
    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (string.IsNullOrEmpty(token)) return;

    using var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FlightPriceMonitor", "1.0"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var repoOwner = "andersonharlen";
    var repoName = "flight-price-monitor";
    var path = "voos.json";
    var url = $"https://api.github.com/repos/{repoOwner}/{repoName}/contents/{path}";

    string? sha = null;
    var getResponse = await client.GetAsync(url);
    if (getResponse.IsSuccessStatusCode)
    {
        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(getBody);
        if (doc.RootElement.TryGetProperty("sha", out var shaProp))
        {
            sha = shaProp.GetString();
        }
    }

    var bytes = Encoding.UTF8.GetBytes(conteudoJson);
    var base64Content = Convert.ToBase64String(bytes);

    var payload = new Dictionary<string, object>
    {
        { "message", "Atualização automática de voos via bot [skip ci]" },
        { "content", base64Content },
        { "branch", "main" }
    };

    if (!string.IsNullOrEmpty(sha))
    {
        payload.Add("sha", sha);
    }

    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    await client.PutAsync(url, content);
}

record VooConfig(
    [property: JsonPropertyName("Trecho")] string Trecho, 
    [property: JsonPropertyName("PrecoMaximo")] decimal PrecoMaximo, 
    [property: JsonPropertyName("Telefone")] string Telefone,
    [property: JsonPropertyName("PendenteEnvio")] bool PendenteEnvio
);
