using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Configura a porta que a Render exige (variável PORT ou 8080)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");

var app = builder.Build();

var arquivoVoos = "voos.json";

// Rota de Teste para ver se o app está no ar
app.MapGet("/", () => "API de Monitoramento de Voos Rodando!");

// Rota que vai receber o webhook do WhatsApp (Evolution API)
app.MapPost("/webhook", async (HttpContext context) =>
{
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        
        // Aqui você pode processar a mensagem que veio do WhatsApp e salvar no voos.json
        Console.WriteLine($"[WEBHOOK] Mensagem recebida: {body}");

        return Results.Ok(new { status = "recebido" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { erro = ex.Message });
    }
});

app.Run();
