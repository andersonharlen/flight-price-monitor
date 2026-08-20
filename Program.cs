using System.Text;
using System.Text.Json;

// Este código agora roda como um script: ele verifica os voos e dispara o alerta
var apiUrL = Environment.GetEnvironmentVariable("EVOLUTION_API_URL");
var apiKey = Environment.GetEnvironmentVariable("EVOLUTION_API_KEY");

Console.WriteLine($"[MONITOR] Iniciando verificação com API em: {apiUrL}");

// Exemplo: Disparar uma mensagem de alerta
await EnviarMensagemWhatsAppAsync(apiUrL!, apiKey!, "5579981394290", "✈️ Monitoramento rodando via GitHub Actions! Promoções em breve...");

async Task EnviarMensagemWhatsAppAsync(string url, string key, string numero, string texto)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("apikey", key);

    var payload = new { number = numero, text = texto };
    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    try {
        var response = await client.PostAsync($"{url}/message/sendText/voos", content);
        Console.WriteLine($"[SUCESSO] Status: {response.StatusCode}");
    } catch (Exception ex) {
        Console.WriteLine($"[ERRO]: {ex.Message}");
    }
}
