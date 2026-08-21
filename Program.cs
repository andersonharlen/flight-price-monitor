using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");
var app = builder.Build();

var arquivoVoos = "voos.json";

// Rota de Teste
app.MapGet("/", () => "API de Monitoramento de Voos Rodando!");

// Rota que recebe a mensagem do WhatsApp (Webhook)
app.MapPost("/webhook", async (HttpContext context) =>
{
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Tenta extrair o texto da mensagem dependendo do formato da Evolution API
        if (root.TryGetProperty("data", out var data) && 
            data.TryGetProperty("message", out var msg) && 
            msg.TryGetProperty("conversation", out var textProp))
        {
            var textoMensagem = textProp.GetString();
            var remetente = data.GetProperty("key").GetProperty("remoteJid").GetString() ?? "";

            // Exemplo de comando no WhatsApp: "CADASTRAR GRU-MIA 3000"
            if (textoMensagem != null && textoMensagem.StartsWith("CADASTRAR", StringComparison.OrdinalIgnoreCase))
            {
                var partes = textoMensagem.Split(' ');
                if (partes.Length >= 3)
                {
                    var trecho = partes[1].ToUpper();
                    if (decimal.TryParse(partes[2], out var precoMaximo))
                    {
                        var telefone = remetente.Split('@')[0];

                        // Ler voos atuais
                        List<VooConfig> voos = new();
                        if (File.Exists(arquivoVoos))
                        {
                            var jsonExistente = await File.ReadAllTextAsync(arquivoVoos);
                            voos = JsonSerializer.Deserialize<List<VooConfig>>(jsonExistente) ?? new();
                        }

                        // Adiciona novo voo
                        voos.Add(new VooConfig(trecho, precoMaximo, telefone));
                        await File.WriteAllTextAsync(arquivoVoos, JsonSerializer.Serialize(voos, new JsonSerializerOptions { WriteIndented = true }));

                        Console.WriteLine($"[SUCESSO] Voo {trecho} cadastrado para {telefone} com teto R$ {precoMaximo}");
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

app.Run();

record VooConfig(string Trecho, decimal PrecoMaximo, string Telefone);
