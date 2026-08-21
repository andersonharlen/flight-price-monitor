// No topo do seu projeto, o script que o GitHub vai rodar:
var json = await File.ReadAllTextAsync("voos.json");
var voos = JsonSerializer.Deserialize<List<Voo>>(json);

foreach (var voo in voos) {
    var precoAtual = BuscarPrecoVoo(voo.Trecho); // Aqui entra sua lógica de busca
    if (precoAtual <= voo.PrecoLimite) {
        await EnviarMensagemWhatsAppAsync("Promoção encontrada!", voo.Telefone);
    }
}
