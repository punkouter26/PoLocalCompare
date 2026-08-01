// Theme switching. Three states are possible and they are not the same thing:
//   "system" — no stored choice; follow prefers-color-scheme and keep following it as it changes.
//   "light" / "dark" — an explicit user choice, stamped onto <html data-theme> so the
//   [data-theme] rules in app.css override the prefers-color-scheme block.
//
// Radzen ships a separate stylesheet per theme rather than CSS custom properties, so its
// <link> href has to be swapped alongside the attribute; leaving it pinned to dark.css was
// what made a light theme impossible before.
(() => {
    const KEY = 'po-theme';
    const RADZEN = {
        light: '_content/Radzen.Blazor/css/default.css',
        dark: '_content/Radzen.Blazor/css/dark.css',
    };

    const systemPrefersDark = () =>
        window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;

    const stored = () => {
        try {
            const v = localStorage.getItem(KEY);
            return v === 'light' || v === 'dark' ? v : null;
        } catch {
            // Private mode / blocked storage: fall back to following the system.
            return null;
        }
    };

    // The theme actually being rendered, resolving "system" to a concrete value.
    const effective = () => stored() ?? (systemPrefersDark() ? 'dark' : 'light');

    const paint = () => {
        const theme = effective();
        const root = document.documentElement;

        // Only stamp the attribute for an explicit choice. Leaving it off when following the
        // system lets the prefers-color-scheme block stay in charge, so a viewer who changes
        // their OS setting mid-session sees it apply without a reload.
        if (stored()) {
            root.setAttribute('data-theme', theme);
        } else {
            root.removeAttribute('data-theme');
        }

        const link = document.getElementById('radzen-theme');
        if (link) {
            const href = RADZEN[theme];
            if (!link.getAttribute('href').endsWith(href.split('/').pop())) {
                link.setAttribute('href', href);
            }
        }
    };

    window.poTheme = {
        // "light" | "dark" | "system"
        current: () => stored() ?? 'system',
        effective,
        set(theme) {
            try {
                if (theme === 'system') {
                    localStorage.removeItem(KEY);
                } else {
                    localStorage.setItem(KEY, theme);
                }
            } catch {
                // Storage unavailable — the choice still applies for this page view.
            }
            paint();
            return effective();
        },
    };

    paint();

    // Keep following the OS while no explicit choice is stored.
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
            if (!stored()) paint();
        });
    }
})();
