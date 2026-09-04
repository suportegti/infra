// RDS Monitor — pequenas interações de UI que não precisam de um circuito
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
