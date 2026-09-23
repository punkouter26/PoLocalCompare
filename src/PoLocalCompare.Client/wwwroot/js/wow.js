/**
 * wow.js — DOM-level motion: route morphs, the holographic model card, the leaderboard
 * reshuffle, liquid glass and the bracket comet.
 *
 * Everything here animates transform and opacity (or hands the work to the browser's own
 * View Transitions machinery), so it runs on the compositor and never contends with the WebGPU
 * device WebLLM generates on. The one GPU-heavier treatment — the liquid-glass refraction — is
 * switched off by CSS whenever the GPU lease is held (html[data-gpu="busy"], set by util.js).
 * The canvas and shader effects live in fx.js; this file owns no canvas.
 *
 * A classic script rather than a module because the navigation hook has to be listening before
 * the first click, and Blazor's interop reaches window.poWow.* directly.
 *
 * Everything respects prefers-reduced-motion by not animating at all.
 */
window.poWow = (() => {
    const reduced = () => {
        try {
            return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        } catch {
            return false;
        }
    };

    // ── 1. View Transitions ──────────────────────────────────────────────────
    //
    // Blazor's router swaps the DOM on its own schedule, so a transition cannot just wrap
    // Blazor.navigateTo: the new page renders a frame or two later, and the destination
    // often shows a loading state first. The transition callback therefore navigates and then
    // waits for a selector that only exists once the destination has real content — capped,
    // because the browser freezes the old snapshot until the callback settles.

    const WAIT_CAP_MS = 1200;

    function waitFor(selector) {
        return new Promise(resolve => {
            if (!selector || document.querySelector(selector)) {
                requestAnimationFrame(() => resolve());
                return;
            }
            const done = () => {
                observer.disconnect();
                clearTimeout(timer);
                resolve();
            };
            const observer = new MutationObserver(() => {
                if (document.querySelector(selector)) done();
            });
            observer.observe(document.getElementById('app') ?? document.body, { childList: true, subtree: true });
            const timer = setTimeout(done, WAIT_CAP_MS);
        });
    }

    function blazorNavigate(url) {
        if (window.Blazor?.navigateTo) window.Blazor.navigateTo(url);
        else window.location.assign(url);
    }

    /**
     * Navigates with a morph. Elements sharing a view-transition-name across the two pages
     * (app.css) fly from where they were to where they land; everything else cross-fades.
     *
     * @param {string} url
     * @param {string} [waitForSelector] present only once the destination has content
     * @param {Element} [hero] marked as the shared element for this one navigation
     */
    function navigate(url, waitForSelector, hero) {
        if (!document.startViewTransition || reduced()) {
            blazorNavigate(url);
            return;
        }

        hero?.classList.add('po-vt-hero');
        const transition = document.startViewTransition(async () => {
            hero?.classList.remove('po-vt-hero');
            blazorNavigate(url);
            await waitFor(waitForSelector);
        });
        transition.finished.catch(() => { /* skipped or superseded: the navigation still happened */ });
    }

    // Links opt in with data-vt="<selector to wait for>"; the hero is the closest
    // [data-vt-hero] or the link itself. Capture phase + preventDefault: Blazor's own anchor
    // handler checks defaultPrevented and stands down, while @onclick handlers still run.
    document.addEventListener('click', event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const link = event.target instanceof Element ? event.target.closest('a[data-vt]') : null;
        if (!link || link.target === '_blank') return;

        const href = link.getAttribute('href');
        if (!href || !document.startViewTransition || reduced()) return;

        event.preventDefault();
        navigate(href, link.dataset.vt, link.closest('[data-vt-hero]') ?? link);
    }, true);

    // ── 2. Holographic model card ────────────────────────────────────────────

    /**
     * Pointer-tilts a card and moves its foil sheen with the angle. The values go out as CSS
     * custom properties and every visual rule lives in the stylesheet, which is the app's rule
     * for dynamic values. rAF-throttled: pointermove fires far faster than a frame.
     */
    function holo(selector) {
        const card = document.querySelector(selector);
        if (!card || card.dataset.holo === 'on' || reduced()) return;
        card.dataset.holo = 'on';

        let frame = 0;
        let last = null;

        const apply = () => {
            frame = 0;
            if (!last) return;
            const rect = card.getBoundingClientRect();
            const x = Math.min(1, Math.max(0, (last.clientX - rect.left) / rect.width));
            const y = Math.min(1, Math.max(0, (last.clientY - rect.top) / rect.height));
            card.style.setProperty('--holo-rx', `${((0.5 - y) * 10).toFixed(2)}deg`);
            card.style.setProperty('--holo-ry', `${((x - 0.5) * 14).toFixed(2)}deg`);
            card.style.setProperty('--holo-mx', `${(x * 100).toFixed(1)}%`);
            card.style.setProperty('--holo-my', `${(y * 100).toFixed(1)}%`);
            card.style.setProperty('--holo-angle', `${Math.round(x * 180 + y * 90)}deg`);
        };

        card.addEventListener('pointermove', e => {
            last = e;
            card.classList.add('profile__card--live');
            if (!frame) frame = requestAnimationFrame(apply);
        });
        card.addEventListener('pointerleave', () => {
            last = null;
            card.classList.remove('profile__card--live');
            for (const p of ['--holo-rx', '--holo-ry', '--holo-mx', '--holo-my', '--holo-angle']) {
                card.style.removeProperty(p);
            }
        });
    }

    const TIER_COLOURS = {
        prismatic: ['#ff6ec7', '#7d96ff', '#12b8cf', '#2ddc84', '#eab308'],
        gold: ['#8a6100', '#eab308', '#ffe57f', '#eab308'],
        silver: ['#5b6475', '#c9d1dc', '#f4f6f9', '#aab4c3'],
        bronze: ['#5a3a1f', '#b0703a', '#e0a36b', '#8c5a2e'],
    };

    /**
     * Draws the model's card to a PNG and downloads it. Canvas2D, on demand, from a click —
     * not a render loop. Fixed tier palettes rather than theme tokens: a trading card should
     * look the same whoever saves it.
     */
    function exportCard(data) {
        const W = 600;
        const H = 840;
        const canvas = document.createElement('canvas');
        canvas.width = W;
        canvas.height = H;
        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        const colours = TIER_COLOURS[data.tier] ?? TIER_COLOURS.bronze;

        const frame = ctx.createLinearGradient(0, 0, W, H);
        colours.forEach((c, i) => frame.addColorStop(i / (colours.length - 1), c));
        ctx.fillStyle = frame;
        roundRect(ctx, 0, 0, W, H, 36);
        ctx.fill();

        ctx.fillStyle = '#0d1422';
        roundRect(ctx, 22, 22, W - 44, H - 44, 24);
        ctx.fill();

        // Foil band across the art box.
        const foil = ctx.createLinearGradient(40, 120, W - 40, 420);
        colours.forEach((c, i) => foil.addColorStop(i / (colours.length - 1), c));
        ctx.globalAlpha = 0.85;
        ctx.fillStyle = foil;
        roundRect(ctx, 48, 120, W - 96, 300, 18);
        ctx.fill();
        ctx.globalAlpha = 1;

        ctx.fillStyle = '#ffffff';
        ctx.font = '800 30px system-ui, sans-serif';
        ctx.fillText(fit(ctx, data.name, W - 180), 52, 84);

        ctx.textAlign = 'right';
        ctx.font = '800 34px system-ui, sans-serif';
        ctx.fillText(data.rank > 0 ? `#${data.rank}` : '—', W - 52, 86);
        ctx.textAlign = 'left';

        ctx.font = '900 110px system-ui, sans-serif';
        ctx.fillStyle = 'rgba(13, 20, 34, 0.82)';
        ctx.textAlign = 'center';
        ctx.fillText(String(Math.round(data.elo)), W / 2, 310);
        ctx.font = '700 22px system-ui, sans-serif';
        ctx.fillText(`ELO  ±${Math.round(data.interval)}`, W / 2, 360);
        ctx.textAlign = 'left';

        const stats = [
            ['Record', data.record],
            ['Win rate', data.winRate],
            ['Duels', String(data.duels)],
            ['Type', data.type],
        ];
        ctx.font = '600 20px system-ui, sans-serif';
        stats.forEach(([label, value], i) => {
            const y = 490 + i * 62;
            ctx.fillStyle = '#8b96a8';
            ctx.fillText(label.toUpperCase(), 56, y);
            ctx.fillStyle = '#ffffff';
            ctx.textAlign = 'right';
            ctx.fillText(value, W - 56, y);
            ctx.textAlign = 'left';
            ctx.fillStyle = 'rgba(255,255,255,0.08)';
            ctx.fillRect(56, y + 20, W - 112, 1);
        });

        ctx.fillStyle = colours[1];
        ctx.font = '800 16px system-ui, sans-serif';
        ctx.fillText(`${data.tier.toUpperCase()} · POLOCALCOMPARE`, 56, H - 56);

        canvas.toBlob(blob => {
            if (!blob) return;
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `${String(data.name).replace(/[^a-z0-9]+/gi, '-').toLowerCase()}-card.png`;
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 10000);
        }, 'image/png');
    }

    function roundRect(ctx, x, y, w, h, r) {
        ctx.beginPath();
        if (ctx.roundRect) {
            ctx.roundRect(x, y, w, h, r);
        } else {
            ctx.rect(x, y, w, h);
        }
    }

    function fit(ctx, text, max) {
        let value = String(text ?? '');
        while (value.length > 4 && ctx.measureText(value).width > max) value = value.slice(0, -2);
        return value === String(text ?? '') ? value : `${value}…`;
    }

    // ── 3. Leaderboard reshuffle ─────────────────────────────────────────────

    const RANKS_KEY = 'polocalcompare.lastRanks';

    /**
     * Compares the board against what this viewer saw last time, stores the new standing, and
     * returns what moved: { modelId: { from, to, narrowedFrom } }. Per-viewer memory in
     * localStorage is the right store here — "since you last looked" is about one person.
     * First visit (or storage blocked) returns nothing, so nothing animates.
     *
     * @param {{id: string, rank: number, interval: number}[]} rows
     */
    function rankDiff(rows) {
        let previous = null;
        try {
            previous = JSON.parse(window.localStorage.getItem(RANKS_KEY) ?? 'null');
        } catch {
            previous = null;
        }

        const next = {};
        const moved = {};
        for (const row of rows) {
            next[row.id] = { rank: row.rank, interval: row.interval };
            const before = previous?.[row.id];
            if (!before) continue;
            const change = {};
            if (before.rank !== row.rank) {
                change.from = before.rank;
                change.to = row.rank;
            }
            if (row.interval < before.interval - 0.5) change.narrowedFrom = before.interval;
            if (Object.keys(change).length) moved[row.id] = change;
        }

        try {
            window.localStorage.setItem(RANKS_KEY, JSON.stringify(next));
        } catch {
            // Not remembered: the next visit simply has nothing to compare against.
        }
        return moved;
    }

    /**
     * FLIP: every moved row is already in its new place; play it in from where its old rank
     * sits on screen today. Measured from the rows themselves, so it is right in the table and
     * in the stacked card layout below 640px alike.
     *
     * @param {Record<string, {from?: number, to?: number}>} moved
     */
    function flipRows(moved) {
        if (reduced()) return;
        const rows = Array.from(document.querySelectorAll('tr[data-rank-row]'));
        const tops = rows.map(r => r.getBoundingClientRect().top);

        let delay = 0;
        for (const row of rows) {
            const change = moved[row.dataset.rankRow];
            if (!change?.from || !change.to) continue;

            const fromIndex = Math.min(rows.length - 1, change.from - 1);
            const offset = fromIndex >= 0 && fromIndex < tops.length
                ? tops[fromIndex] - tops[change.to - 1]
                : 24;

            row.animate(
                [
                    { transform: `translateY(${offset}px)`, opacity: 0.6 },
                    { transform: 'translateY(0)', opacity: 1 },
                ],
                { duration: 720, delay, easing: 'cubic-bezier(.2,.9,.25,1.15)', fill: 'backwards' });
            delay += 60;
        }
    }

    // ── 4. Liquid glass ──────────────────────────────────────────────────────

    /**
     * backdrop-filter: url(#svg) is Chromium-only, and @supports cannot be trusted to say so —
     * other engines parse the declaration and then render nothing, which would drop the plain
     * blur too. So it is gated on the engine, as a class, and CSS keeps the plain blur for
     * everyone else. Pointer position drives the specular highlight via --gx/--gy.
     */
    function initGlass() {
        const brands = navigator.userAgentData?.brands ?? [];
        if (brands.some(b => /Chromium/i.test(b.brand))) {
            document.documentElement.classList.add('po-liquid');
        }
        if (reduced()) return;

        let frame = 0;
        let pending = null;
        document.addEventListener('pointermove', e => {
            pending = e;
            if (frame) return;
            frame = requestAnimationFrame(() => {
                frame = 0;
                const target = pending?.target instanceof Element ? pending.target.closest('.po-glass') : null;
                if (!target) return;
                const rect = target.getBoundingClientRect();
                target.style.setProperty('--gx', `${(((pending.clientX - rect.left) / rect.width) * 100).toFixed(1)}%`);
                target.style.setProperty('--gy', `${(((pending.clientY - rect.top) / rect.height) * 100).toFixed(1)}%`);
            });
        }, { passive: true });
    }

    // ── 5. Bracket comet ─────────────────────────────────────────────────────

    function centreOf(selector) {
        const el = document.querySelector(selector);
        if (!el) return null;
        const r = el.getBoundingClientRect();
        return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    }

    /**
     * A point of light from a match winner to the slot it now fills in the next round, along a
     * shallow arc. A transformed <span> animated by WAAPI — compositor-only, so it is safe even
     * if the next browser match is already starting on the GPU.
     */
    function comet(fromSelector, toSelector) {
        if (reduced()) return;
        const from = centreOf(fromSelector);
        const to = centreOf(toSelector);
        if (!from || !to) return;

        const dot = document.createElement('span');
        dot.className = 'po-comet';
        dot.setAttribute('aria-hidden', 'true');
        document.body.appendChild(dot);

        const lift = Math.min(60, Math.abs(to.x - from.x) * 0.25);
        const mid = { x: (from.x + to.x) / 2, y: Math.min(from.y, to.y) - lift };
        const at = p => `translate(${p.x}px, ${p.y}px) translate(-50%, -50%)`;

        dot.animate(
            [
                { transform: `${at(from)} scale(0.4)`, opacity: 0 },
                { transform: `${at(from)} scale(1)`, opacity: 1, offset: 0.1 },
                { transform: `${at(mid)} scale(1.25)`, opacity: 1, offset: 0.55 },
                { transform: `${at(to)} scale(0.6)`, opacity: 0 },
            ],
            { duration: 900, easing: 'cubic-bezier(.45,.05,.3,1)' }
        ).finished.finally(() => dot.remove());
    }

    /** The loser of a just-decided match tips and drops, then settles back dimmed. */
    function knock(selector) {
        if (reduced()) return;
        const el = document.querySelector(selector);
        el?.animate(
            [
                { transform: 'none' },
                { transform: 'translateY(6px) rotate(-4deg)', offset: 0.3 },
                { transform: 'translateY(14px) rotate(3deg)', opacity: 0.35, offset: 0.65 },
                { transform: 'none' },
            ],
            { duration: 820, easing: 'cubic-bezier(.3,.7,.4,1)' });
    }

    initGlass();

    return { navigate, holo, exportCard, rankDiff, flipRows, comet, knock };
})();
