using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");
var app = builder.Build();

var arquivoVoos = "voos.json";

// Pega as credenciais da Evolution API das variáveis de ambiente da Render (ou define fixas se preferir)
var evolutionApiUrl = Environment.GetEnvironmentVariable("EVOLUTION_API_URL") ?? "https://sua-evolution-api.com";
var evolutionApiKey = Environment.GetEnvironmentVariable("EVOLUTION_API_KEY") ?? "sua-api-key";
var instanceName = Environment.GetEnvironmentVariable("EVOLUTION_INSTANCE") ?? "voos";

app.MapGet("/", () => "API de Monitoramento de Voos Rodando!");

app.MapPost("/webhook", async (HttpContext context) =>
{
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data) && 
            data.TryGetProperty("message", out var msg) && 
            msg.TryGetProperty("conversation", out var textProp))
        {
            var textoMensagem = textProp.GetString()?.Trim() ?? "";
            var remetente = data.GetProperty("key").GetProperty("remoteJid").GetString() ?? "";

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

                        voos.Add(new VooConfig(trecho, precoMaximo, telefone));
                        await File.WriteAllTextAsync(arquivoVoos, JsonSerializer.Serialize(voos, new JsonSerializerOptions { WriteIndented = true }));

                        Console.WriteLine($"[SUCESSO] Voo {trecho} cadastrado para {telefone} com teto R$ {precoMaximo}");
                        
                        // Envia a resposta de volta no WhatsApp
                        await EnviarMensagemWhatsAppAsync(remetente, $"✅ Alerta cadastrado com sucesso!\n\n✈️ Trecho: {trecho}\n💰 Preço Teto: R$ {precoMaximo:N2}");
                    }
                }
            }
        }

        return Results.Ok(new { status = "processado" });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO WEBHOOK]: {ex.Message}");
        return Results.BadRequest(new { erro = ex.Message });
    }
});

async Task EnviarMensagemWhatsAppAsync(string remoteJid, string mensagem)
{
    try
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("apikey", evolutionApiKey);

        var payload = new
        {
            number = remoteJid,
            text = mensagem
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        // Ajuste a URL base da sua Evolution API aqui se necessário
        await client.PostAsync($"{evolutionApiUrl}/message/sendText/{instanceName}", content);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO AO ENVIAR WHATSAPP]: {ex.Message}");
    }
}

app.Run();

record VooConfig(
    [property: JsonPropertyName("Trecho")] string Trecho, 
    [property: JsonPropertyName("PrecoMaximo")] decimal PrecoMaximo, 
    [property: JsonPropertyName("Telefone")] string Telefone
);
