// RDSManager — conexão das telas interativas (Blazor Server).
//
// As telas de monitoramento ficam ligadas ao servidor por WebSocket. Quando a
// conexão cai (rede, VPN, notebook suspenso, portal reiniciado), o padrão do
// Blazor é cobrir a tela e, depois de algumas tentativas, exigir F5. Aqui:
//  • reconecta várias vezes, mostrando só uma faixa discreta no topo;
//  • se o servidor não reconhece mais a tela (portal reiniciado/atualizado),
//    recarrega a página sozinho;
//  • se esgotar as tentativas, espera o servidor responder e recarrega;
//  • ao voltar para a aba ou a rede voltar, tenta de imediato.
(function () {
    var TENTATIVAS = 60;          // 60 x 3 s = 3 min reconectando na mesma tela
    var INTERVALO_MS = 3000;
    var recarregando = false;

    function modal() { return document.getElementById('components-reconnect-modal'); }

    function recarregar() {
        if (recarregando) return;
        recarregando = true;
        var m = modal();
        if (m) m.classList.add('rds-recarregando');
        setTimeout(function () { location.reload(); }, 400);
    }

    // Só recarrega quando o servidor responde, para não trocar a tela por uma página de erro.
    function recarregarQuandoServidorVoltar() {
        if (recarregando) return;
        fetch('/favicon.svg', { method: 'HEAD', cache: 'no-store' })
            .then(function (r) { if (r.ok) recarregar(); else setTimeout(recarregarQuandoServidorVoltar, 5000); })
            .catch(function () { setTimeout(recarregarQuandoServidorVoltar, 5000); });
    }

    function tentarAgora() {
        var m = modal();
        if (!m || recarregando) return;
        if (m.classList.contains('components-reconnect-rejected')) return recarregar();
        if (m.classList.contains('components-reconnect-failed')) {
            // Tenta religar a mesma tela; se o servidor não a reconhece mais, recarrega.
            Blazor.reconnect().then(function (ok) { if (!ok) recarregar(); }, recarregarQuandoServidorVoltar);
        }
    }

    function observar() {
        var m = modal();
        if (!m) return;
        new MutationObserver(function () {
            if (m.classList.contains('components-reconnect-rejected')) recarregar();
            else if (m.classList.contains('components-reconnect-failed')) recarregarQuandoServidorVoltar();
        }).observe(m, { attributes: true, attributeFilter: ['class'] });
    }

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') tentarAgora();
    });
    window.addEventListener('online', tentarAgora);

    Blazor.start({
        circuit: {
            reconnectionOptions: { maxRetries: TENTATIVAS, retryIntervalMilliseconds: INTERVALO_MS },
            configureSignalR: function (builder) {
                // Tolerante a abas em segundo plano (o servidor espera até 2 min; ver Program.cs)
                builder.withServerTimeout(60000).withKeepAliveInterval(15000);
            }
        }
    }).then(observar, observar);
})();
