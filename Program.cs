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

                        // Remove duplicado do mesmo telefone/trecho se já existir, ou adiciona novo
                        voos.RemoveAll(v => v.Trecho == trecho && v.Telefone == telefone);
                        voos.Add(new VooConfig(trecho, precoMaximo, telefone, true));

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

record VooConfig(
    [property: JsonPropertyName("Trecho")] string Trecho, 
    [property: JsonPropertyName("PrecoMaximo")] decimal PrecoMaximo, 
    [property: JsonPropertyName("Telefone")] string Telefone,
    [property: JsonPropertyName("PendenteEnvio")] bool PendenteEnvio
);
