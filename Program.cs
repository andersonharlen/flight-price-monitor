using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");
var app = builder.Build();

var arquivoVoos = "voos.json";

app.MapGet("/", () => "API de Monitoramento de Voos Rodando!");

// Webhook para receber comandos do WhatsApp
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

            // 1. Comando CADASTRAR: CADASTRAR MGF-AJU 800
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
                        
                        // Aqui você poderia disparar o envio de mensagem de confirmação de volta no WhatsApp
                    }
                }
            }
            // 2. Comando LISTAR (Gera o texto pronto para o LinkedIn)
            else if (textoMensagem.Equals("LISTAR", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(arquivoVoos))
                {
                    var jsonExistente = await File.ReadAllTextAsync(arquivoVoos);
                    var voos = JsonSerializer.Deserialize<List<VooConfig>>(jsonExistente) ?? new();

                    if (voos.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("🚀 *Voos Monitorados Atualmente:*");
                        sb.AppendLine();
                        foreach (var v in voos)
                        {
                            sb.AppendLine($"✈️ Trecho: *{v.Trecho}* | Teto Alvo: *R$ {v.PrecoMaximo:N2}*");
                        }
                        sb.AppendLine();
                        sb.AppendLine("💡 _Desenvolvendo automações em C# e .NET para monitoramento de preços em tempo real! #dotnet #csharp #dev_");

                        // Aqui imprimimos no console/logs para você ver o texto pronto
                        Console.WriteLine("\n--- TEXTO PARA O LINKEDIN ---\n" + sb.ToString() + "\n-----------------------------\n");
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

record VooConfig(
    [property: JsonPropertyName("Trecho")] string Trecho, 
    [property: JsonPropertyName("PrecoMaximo")] decimal PrecoMaximo, 
    [property: JsonPropertyName("Telefone")] string Telefone
);
