// Small page helpers. The color mode itself is rendered by the server (Hathor renders <body
// data-theme="light|dark|system"> from the signed-in user's account); this script only applies
// a mode changed on the account page without a reload.

// Small API for interactive pages (the account page).
window.anubis = {
    // Applies a color mode ("light" | "dark" | "system") the page has already saved on the account.
    setColorScheme(scheme) {
        document.body.dataset.theme = scheme;
    },
};

document.addEventListener('click', (event) => {
    // "Sign out" navigation item: post the hidden sign-out form instead of following the link.
    const logout = event.target.closest?.('a[href="/logout"]');
    const form = logout && document.querySelector('form[data-logout-form]');
    if (form) {
        event.preventDefault();
        form.submit();
        return;
    }

    // Native <details> menus (Hathor Menu, the phone navigation): close the others,
    // all of them on an outside click, and a menu once a link in it is chosen.
    const current = event.target.closest?.('details');
    const link = event.target.closest?.('a[href]');
    for (const menu of document.querySelectorAll('details[open]')) {
        if (menu !== current || (link && menu.contains(link))) {
            menu.removeAttribute('open');
        }
    }
});

// Escape closes an open menu (the phone navigation drawer, Hathor Menu).
document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') {
        return;
    }
    for (const menu of document.querySelectorAll('details[open]')) {
        menu.removeAttribute('open');
        menu.querySelector('summary')?.focus();
    }
});

// <time data-local-time datetime="…Z"> (account history): show in the browser's time zone,
// formatted for the page language.
function localizeTimes() {
    const format = new Intl.DateTimeFormat(document.documentElement.lang || undefined, { dateStyle: 'medium', timeStyle: 'short' });
    for (const time of document.querySelectorAll('time[data-local-time]')) {
        const date = new Date(time.getAttribute('datetime'));
        if (!Number.isNaN(date.getTime())) {
            time.textContent = format.format(date);
            time.title = date.toISOString();
            time.removeAttribute('data-local-time');
        }
    }
}

localizeTimes();
window.Blazor?.addEventListener?.('enhancedload', localizeTimes);
