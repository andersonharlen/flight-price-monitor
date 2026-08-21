using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapPost("/webhook", async (HttpContext ctx) => {
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    
    // Lógica simples: Se a mensagem contiver "Cadastrar"
    if (body.Contains("Cadastrar")) {
        // Extrai o voo (ex: "GRU-LIS 2000") e salva no arquivo voos.json
        var novoVoo = new { Trecho = "GRU-LIS", PrecoLimite = 2000, Telefone = "55..." };
        var lista = new List<object> { novoVoo };
        await File.WriteAllTextAsync("voos.json", JsonSerializer.Serialize(lista));
    }
    return Results.Ok();
});

app.Run();
