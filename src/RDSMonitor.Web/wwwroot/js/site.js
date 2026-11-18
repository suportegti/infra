// RDSManager — pequenas interações de UI que não precisam de um circuito
// Blazor (tema, menu mobile e fechamento de dropdowns). Tudo client-side.

function toggleTheme() {
    var atual = document.documentElement.getAttribute('data-theme') || 'dark';
    var proximo = atual === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', proximo);
    try {
        localStorage.setItem('rdsmonitor-theme', proximo);
    } catch (e) {
        // localStorage indisponível (ex.: modo privado) — ignora silenciosamente.
    }
}

function toggleNav() {
    var shell = document.querySelector('.app-shell');
    if (shell) {
        shell.classList.toggle('nav-open');
    }
}

document.addEventListener('click', function (event) {
    document.querySelectorAll('details.user-menu[open]').forEach(function (menu) {
        if (!menu.contains(event.target)) {
            menu.removeAttribute('open');
        }
    });
});

// ── Olho dos campos de senha ────────────────────────────────────────────────
// Mostra a senha SÓ enquanto o botão estiver pressionado (mouse ou toque) e a
// esconde ao soltar, ao arrastar para fora ou se a janela perder o foco.
// Delegação no document: funciona também nas telas trocadas pela navegação do Blazor.
(function () {
    var ativo = null; // { input, icone }

    function mostrar(botao) {
        var input = botao.parentElement && botao.parentElement.querySelector('input');
        if (!input) return;
        esconder();
        input.type = 'text';
        var icone = botao.querySelector('i');
        if (icone) icone.className = 'fa-solid fa-eye-slash';
        botao.classList.add('pressionado');
        ativo = { input: input, icone: icone, botao: botao };
    }

    function esconder() {
        if (!ativo) return;
        ativo.input.type = 'password';
        if (ativo.icone) ativo.icone.className = 'fa-solid fa-eye';
        ativo.botao.classList.remove('pressionado');
        ativo = null;
    }

    document.addEventListener('pointerdown', function (e) {
        var botao = e.target.closest && e.target.closest('.olho-senha');
        if (!botao) return;
        e.preventDefault();               // mantém o cursor no campo de senha
        mostrar(botao);
    });

    // Sem isso o mousedown tiraria o foco do campo
    document.addEventListener('mousedown', function (e) {
        if (e.target.closest && e.target.closest('.olho-senha')) e.preventDefault();
    });

    ['pointerup', 'pointercancel', 'dragend'].forEach(function (ev) {
        document.addEventListener(ev, esconder);
    });
    document.addEventListener('pointerout', function (e) {
        if (ativo && e.target.closest && e.target.closest('.olho-senha') === ativo.botao) esconder();
    });
    window.addEventListener('blur', esconder);

    // Toque longo no celular não deve abrir o menu de contexto
    document.addEventListener('contextmenu', function (e) {
        if (e.target.closest && e.target.closest('.olho-senha')) e.preventDefault();
    });

    // Segurança: nunca envia o formulário com a senha visível
    document.addEventListener('submit', esconder, true);
})();
