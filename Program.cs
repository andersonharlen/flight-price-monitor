using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Dicionários em memória para gerenciar a sessão do chat e os alertas salvos
var sessoesUsuarios = new Dictionary<string, SessaoUsuario>();
var alertasVoos = new List<AlertaVoo>();

const string EvolutionApiUrl = "http://localhost:8080";
const string ApiKey = "minhasuperchave123";
const string InstanceName = "voos";

// 1. Endpoint do Webhook que recebe as mensagens do WhatsApp
app.MapPost("/webhook", async ([FromBody] JsonElement payload) =>
{
    try
    {
        if (payload.TryGetProperty("event", out var ev) && ev.GetString() == "messages.upsert")
        {
            var data = payload.GetProperty("data");
            var remoteJid = data.GetProperty("key").GetProperty("remoteJid").GetString()!;
            var numeroLimpo = remoteJid.Split('@')[0];

            // Tenta pegar o texto da mensagem normal ou estendida
            string? textoMensagem = null;
            if (data.TryGetProperty("message", out var msgObj))
            {
                if (msgObj.TryGetProperty("conversation", out var conv))
                    textoMensagem = conv.GetString();
                else if (msgObj.TryGetProperty("extendedTextMessage", out var ext) && ext.TryGetProperty("text", out var extText))
                    textoMensagem = extText.GetString();
            }

            if (!string.IsNullOrEmpty(textoMensagem))
            {
                await ProcessarFluxoBotAsync(numeroLimpo, textoMensagem.Trim());
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO WEBHOOK]: {ex.Message}");
    }

    return Results.Ok(new { status = "ok" });
});

app.Run("http://localhost:3000");

// --- LÓGICA DO BOT ---

async Task ProcessarFluxoBotAsync(string numero, string texto)
{
    if (!sessoesUsuarios.ContainsKey(numero))
    {
        sessoesUsuarios[numero] = new SessaoUsuario();
    }

    var sessao = sessoesUsuarios[numero];
    var textoLower = texto.ToLower();

    if (textoLower is "oi" or "olá" or "menu" or "começar" || sessao.Estado == "INICIAL")
    {
        sessao.Estado = "AGUARDANDO_ORIGEM";
        sessao.DadosTemp = new AlertaVoo();
        await EnviarMensagemWhatsAppAsync(numero, "✈️ *Bem-vindo ao Flight Monitor!*\n\nQual é a cidade/aeroporto de **ORIGEM**?");
        return;
    }

    if (sessao.Estado == "AGUARDANDO_ORIGEM")
    {
        sessao.DadosTemp.Origem = texto;
        sessao.Estado = "AGUARDANDO_DESTINO";
        await EnviarMensagemWhatsAppAsync(numero, $"Origem: *{texto}*.\n\nAgora, qual é o **DESTINO** desejado?");
        return;
    }

    if (sessao.Estado == "AGUARDANDO_DESTINO")
    {
        sessao.DadosTemp.Destino = texto;
        sessao.Estado = "AGUARDANDO_PRECO";
        await EnviarMensagemWhatsAppAsync(numero, $"Destino: *{texto}*.\n\nQual é o **valor máximo (R$)** que você aceita pagar?");
        return;
    }

    if (sessao.Estado == "AGUARDANDO_PRECO")
    {
        sessao.DadosTemp.PrecoMaximo = texto;
        sessao.DadosTemp.Numero = numero;

        alertasVoos.Add(sessao.DadosTemp);
        sessao.Estado = "INICIAL";

        await EnviarMensagemWhatsAppAsync(numero, $"✅ *Alerta cadastrado com sucesso!*\n\n" +
                                                 $"🔍 Origem: {sessao.DadosTemp.Origem}\n" +
                                                 $"🎯 Destino: {sessao.DadosTemp.Destino}\n" +
                                                 $"💰 Até: R$ {sessao.DadosTemp.PrecoMaximo}\n\n" +
                                                 $"Te avisarei quando houver promoções!");
    }
}

async Task EnviarMensagemWhatsAppAsync(string numero, string texto)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("apikey", ApiKey);

    var payload = new { number = numero, text = texto };
    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    await client.PostAsync($"{EvolutionApiUrl}/message/sendText/{InstanceName}", content);
}

// Modelos de Dados
class SessaoUsuario
{
    public string Estado { get; set; } = "INICIAL";
    public AlertaVoo DadosTemp { get; set; } = new();
}

class AlertaVoo
{
    public string Numero { get; set; } = "";
    public string Origem { get; set; } = "";
    public string Destino { get; set; } = "";
    public string PrecoMaximo { get; set; } = "";
}
