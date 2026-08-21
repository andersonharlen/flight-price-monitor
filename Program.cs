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
    Console.WriteLine($"[MONITOR] Verificando voo: {voo.Trecho} (Teto: R$ {voo.PrecoMaximo})");

    // Lógica de busca de preço (simulada ou integrada com sua API de voos)
    decimal precoAtual = BuscarPrecoAtualNoSite(voo.Trecho); 

    if (precoAtual <= voo.PrecoMaximo)
    {
        var mensagem = $"🚨 *Promoção Encontrada!*\nTrecho: {voo.Trecho}\nPreço atual: R$ {precoAtual}";
        await EnviarMensagemWhatsAppAsync(client, apiUrl!, voo.Telefone, mensagem);
    }
}

decimal BuscarPrecoAtualNoSite(string trecho)
{
    // Insira aqui sua lógica de cotação ou web scraping
    return 2400; 
}

async Task EnviarMensagemWhatsAppAsync(HttpClient client, string url, string numero, string texto)
{
    var payload = new { number = numero, text = texto };
    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    try 
    {
        // Endpoint padrão da Evolution API para envio de texto
        var response = await client.PostAsync($"{url}/message/sendText/voos", content);
        Console.WriteLine($"[SUCESSO] Alerta enviado para {numero}. Status: {response.StatusCode}");
    } 
    catch (Exception ex) 
    {
        Console.WriteLine($"[ERRO AO ENVIAR]: {ex.Message}");
    }
}

record VooConfig(string Trecho, decimal PrecoMaximo, string Telefone);
