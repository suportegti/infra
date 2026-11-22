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

// ── Mantém o tema escolhido ao navegar entre os menus ───────────────────────
// A navegação do Blazor ("enhanced navigation") atualiza o <html> com o que vem
// do servidor, que não tem data-theme: o atributo sumia e a tela voltava ao tema
// escuro. Sempre que o atributo mudar sem ser pelo botão, reaplica o tema salvo.
function temaSalvo() {
    try { return localStorage.getItem('rdsmonitor-theme') === 'light' ? 'light' : 'dark'; }
    catch (e) { return document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark'; }
}
function aplicarTemaSalvo() {
    var tema = temaSalvo();
    if (document.documentElement.getAttribute('data-theme') !== tema)
        document.documentElement.setAttribute('data-theme', tema);
}
new MutationObserver(aplicarTemaSalvo)
    .observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
if (window.Blazor && Blazor.addEventListener) {
    try { Blazor.addEventListener('enhancedload', aplicarTemaSalvo); } catch (e) { }
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

// ── Cadastro de usuário: campos de senha só para login local ────────────────
// O formulário recebe data-tipo="Local" | "Microsoft365" e o CSS esconde .so-local / .so-m365.
document.addEventListener('change', function (e) {
    var sel = e.target.closest && e.target.closest('select.tipo-autenticacao');
    if (!sel) return;
    var form = sel.closest('form');
    if (form) form.setAttribute('data-tipo', sel.value);
});

// Cadastro de usuário: "Gerar senha temporária e enviar por e-mail" esconde o campo de senha manual
document.addEventListener('change', function (e) {
    var chk = e.target.closest && e.target.closest('input.gerar-senha');
    if (!chk) return;
    var form = chk.closest('form');
    if (form) form.setAttribute('data-gerar', chk.checked ? '1' : '0');
});
