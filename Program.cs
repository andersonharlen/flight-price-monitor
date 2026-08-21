using System.Text;
using System.Text.Json;

var apiUrl = Environment.GetEnvironmentVariable("EVOLUTION_API_URL");
var apiKey = Environment.GetEnvironmentVariable("EVOLUTION_API_KEY");
var arquivoVoos = "voos.json";

if (!File.Exists(arquivoVoos))
{
    Console.WriteLine("[ERRO] Arquivo voos.json não encontrado.");
    return;
}

var jsonContent = await File.ReadAllTextAsync(arquivoVoos);
var voos = JsonSerializer.Deserialize<List<VooConfig>>(jsonContent);

if (voos == null || voos.Count == 0)
{
    Console.WriteLine("[INFO] Nenhum voo cadastrado para monitorar.");
    return;
}

using var client = new HttpClient();
client.DefaultRequestHeaders.Add("apikey", apiKey);

foreach (var voo in voos)
{
    Console.WriteLine($"[MONITOR] Verificando: {voo.Trecho} (Teto: R$ {voo.PrecoMaximo})");

    // Aqui entra a lógica de buscar o preço atual do voo
    decimal precoAtual = BuscarPrecoAtual(voo.Trecho); 

    if (precoAtual <= voo.PrecoMaximo)
    {
        var mensagem = $"🚨 *Promoção Encontrada!*\nTrecho: {voo.Trecho}\nPreço atual: R$ {precoAtual}";
        
        var payload = new { number = voo.Telefone, text = mensagem };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{apiUrl}/message/sendText/voos", content);
        Console.WriteLine($"[SUCESSO] Alerta enviado para {voo.Telefone}. Status: {response.StatusCode}");
    }
}

decimal BuscarPrecoAtual(string trecho)
{
    // Substitua pela sua busca real de preços
    return 2400.00m; 
}

record VooConfig(string Trecho, decimal PrecoMaximo, string Telefone);
