using System;
using System.Net.Http;
using System.Threading.Tasks;

string phone = Environment.GetEnvironmentVariable("WHATSAPP_PHONE") ?? "5500000000000";
string apiKey = Environment.GetEnvironmentVariable("CALLMEBOT_API_KEY") ?? "123456";

string origin = "GRU";
string destination = "FLN";
decimal targetPrice = 300.00m;

Console.WriteLine($"[LOG] Iniciando verificação de passagens: {origin} -> {destination}");

// Simulação de busca de preço
decimal currentPrice = new Random().Next(180, 450); 

Console.WriteLine($"[LOG] Preço Alvo: R$ {targetPrice:F2} | Preço Encontrado: R$ {currentPrice:F2}");

if (currentPrice <= targetPrice)
{
    using var client = new HttpClient();
    
    string message = Uri.EscapeDataString(
        $"🚨 *ALERTA DE PASSAGEM!* ✈️\n\n" +
        $"Rota: *{origin}* ➔ *{destination}*\n" +
        $"Preço encontrado: *R$ {currentPrice:F2}*\n\n" +
        $"Garanta a sua antes que suba!"
    );

    string url = $"https://api.callmebot.com/whatsapp.php?phone={phone}&text={message}&apikey={apiKey}";

    Console.WriteLine("[LOG] Preço abaixo do limite! Disparando notificação...");
    var response = await client.GetAsync(url);

    if (response.IsSuccessStatusCode)
    {
        Console.WriteLine("[SUCCESS] Mensagem enviada para o WhatsApp com sucesso!");
    }
    else
    {
        Console.WriteLine($"[ERROR] Falha ao enviar WhatsApp. Status: {response.StatusCode}");
    }
}
else
{
    Console.WriteLine("[LOG] Preço acima do limite. Nenhum alerta necessário.");
}