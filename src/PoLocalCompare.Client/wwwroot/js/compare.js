// Interop for the Arena comparison tools: sandbox runtime probing, synced scrolling,
// share-card rendering and haptics. Loaded from index.html with a cache-buster.

(() => {
    'use strict';

    // ── Sandbox runtime probe ────────────────────────────────────────────────
    // SandboxedViewport can prepend a tiny reporter script into the iframe's srcdoc. The
    // iframe is sandbox="allow-scripts" with no allow-same-origin, so it has an opaque origin
    // and postMessage is the only channel out — which also means ev.origin is always "null"
    // and cannot be used to authenticate. Instead each frame is handed a fresh random id and
    // only a registered id is dispatched. A model's own HTML could in principle forge a
    // message, but it would have to guess the id, and the only thing it could affect is its
    // own error count.
    const probeHandlers = new Map();

    window.sandboxProbe = {
        register(probeId, dotnetRef) {
            probeHandlers.set(probeId, dotnetRef);
        },
        unregister(probeId) {
            probeHandlers.delete(probeId);
        },
    };

    window.addEventListener('message', (ev) => {
        const data = ev.data;
        if (!data || typeof data !== 'object' || typeof data.__plprobe !== 'string') return;

        const handler = probeHandlers.get(data.__plprobe);
        if (!handler) return;

        handler.invokeMethodAsync('OnProbeMessage', String(data.type || ''), String(data.message || ''))
            .catch(() => probeHandlers.delete(data.__plprobe));
    });

    // ── Synced scrolling for the side-by-side code/diff panes ────────────────
    // Proportional rather than pixel-for-pixel: the two panes hold different amounts of text,
    // so matching raw scrollTop would drift apart immediately. The guard flag stops the two
    // scroll handlers from ping-ponging off each other.
    const syncedPairs = new Map();

    window.syncScrollPanes = function (leftId, rightId) {
        window.unsyncScrollPanes(leftId);

        const left = document.getElementById(leftId);
        const right = document.getElementById(rightId);
        if (!left || !right) return;

        let settling = false;

        const link = (from, to) => () => {
            if (settling) return;
            settling = true;

            const fromRange = from.scrollHeight - from.clientHeight;
            const toRange = to.scrollHeight - to.clientHeight;
            to.scrollTop = fromRange > 0 ? (from.scrollTop / fromRange) * toRange : 0;

            // Release on the next frame: assigning scrollTop queues a scroll event on `to`,
            // and clearing the flag synchronously would let that event bounce straight back.
            requestAnimationFrame(() => { settling = false; });
        };

        const onLeft = link(left, right);
        const onRight = link(right, left);

        left.addEventListener('scroll', onLeft, { passive: true });
        right.addEventListener('scroll', onRight, { passive: true });

        syncedPairs.set(leftId, () => {
            left.removeEventListener('scroll', onLeft);
            right.removeEventListener('scroll', onRight);
        });
    };

    window.unsyncScrollPanes = function (leftId) {
        const teardown = syncedPairs.get(leftId);
        if (teardown) {
            teardown();
            syncedPairs.delete(leftId);
        }
    };

    // ── Haptics ──────────────────────────────────────────────────────────────
    // Best-effort: desktop browsers and iOS Safari have no Vibration API, and a browser may
    // refuse without a prior user gesture. A verdict click is a gesture, so this fires there.
    window.hapticPulse = function (pattern) {
        try {
            if (navigator.vibrate) navigator.vibrate(pattern);
        } catch {
            /* vibration is decoration — never let it surface as an error */
        }
    };

    // ── Clipboard ────────────────────────────────────────────────────────────
    window.copyTextToClipboard = async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    };

    // ── Share card ───────────────────────────────────────────────────────────
    // Renders an OG-sized PNG entirely on the client and downloads it. Fixed dark palette on
    // purpose: this is an exported image, not a page, so it does not follow the viewer's theme.
    const CARD_W = 1200;
    const CARD_H = 630;

    function roundedRect(ctx, x, y, w, h, r) {
        ctx.beginPath();
        ctx.moveTo(x + r, y);
        ctx.arcTo(x + w, y, x + w, y + h, r);
        ctx.arcTo(x + w, y + h, x, y + h, r);
        ctx.arcTo(x, y + h, x, y, r);
        ctx.arcTo(x, y, x + w, y, r);
        ctx.closePath();
    }

    function wrapText(ctx, text, maxWidth, maxLines) {
        const words = String(text || '').split(/\s+/).filter(Boolean);
        const lines = [];
        let line = '';

        for (const word of words) {
            const candidate = line ? line + ' ' + word : word;
            if (ctx.measureText(candidate).width > maxWidth && line) {
                lines.push(line);
                line = word;
                if (lines.length === maxLines) break;
            } else {
                line = candidate;
            }
        }

        if (lines.length < maxLines && line) lines.push(line);

        if (lines.length === maxLines) {
            let last = lines[maxLines - 1];
            while (last && ctx.measureText(last + '…').width > maxWidth) {
                last = last.slice(0, -1);
            }
            // Only mark truncation when words were actually left over.
            const consumed = lines.join(' ').split(/\s+/).length;
            if (consumed < words.length) lines[maxLines - 1] = last + '…';
        }

        return lines;
    }

    /**
     * @param {object} card - { kind, prompt, leftName, rightName, leftStat, rightStat,
     *                          winner ('left'|'right'|'none'), badge, footer, fileName }
     */
    window.renderShareCard = function (card) {
        const canvas = document.createElement('canvas');
        canvas.width = CARD_W;
        canvas.height = CARD_H;
        const ctx = canvas.getContext('2d');

        const bg = ctx.createLinearGradient(0, 0, CARD_W, CARD_H);
        bg.addColorStop(0, '#0b1020');
        bg.addColorStop(0.55, '#131b2d');
        bg.addColorStop(1, '#0a0a0a');
        ctx.fillStyle = bg;
        ctx.fillRect(0, 0, CARD_W, CARD_H);

        ctx.strokeStyle = 'rgba(83, 166, 255, 0.25)';
        ctx.lineWidth = 2;
        roundedRect(ctx, 24, 24, CARD_W - 48, CARD_H - 48, 24);
        ctx.stroke();

        // Header
        ctx.fillStyle = '#53a6ff';
        ctx.font = '600 26px system-ui, -apple-system, Segoe UI, sans-serif';
        ctx.fillText('PoLocalCompare', 64, 92);

        ctx.fillStyle = '#9ea8bb';
        ctx.font = '400 22px system-ui, -apple-system, Segoe UI, sans-serif';
        ctx.textAlign = 'right';
        ctx.fillText(String(card.kind || 'Duel result'), CARD_W - 64, 92);
        ctx.textAlign = 'left';

        // Prompt
        ctx.fillStyle = '#f5f7fb';
        ctx.font = '700 40px system-ui, -apple-system, Segoe UI, sans-serif';
        const promptLines = wrapText(ctx, card.prompt, CARD_W - 128, 2);
        promptLines.forEach((line, i) => ctx.fillText(line, 64, 168 + i * 52));

        // Contender panels
        const panelY = 300;
        const panelH = 190;
        const panelW = (CARD_W - 128 - 56) / 2;
        const sides = [
            { x: 64, name: card.leftName, stat: card.leftStat, won: card.winner === 'left' },
            { x: 64 + panelW + 56, name: card.rightName, stat: card.rightStat, won: card.winner === 'right' },
        ];

        for (const side of sides) {
            ctx.fillStyle = side.won ? 'rgba(45, 220, 132, 0.14)' : 'rgba(255, 255, 255, 0.05)';
            roundedRect(ctx, side.x, panelY, panelW, panelH, 18);
            ctx.fill();

            ctx.strokeStyle = side.won ? '#2ddc84' : 'rgba(255, 255, 255, 0.12)';
            ctx.lineWidth = side.won ? 3 : 1.5;
            roundedRect(ctx, side.x, panelY, panelW, panelH, 18);
            ctx.stroke();

            ctx.fillStyle = '#f5f7fb';
            ctx.font = '700 34px system-ui, -apple-system, Segoe UI, sans-serif';
            const nameLines = wrapText(ctx, side.name, panelW - 56, 2);
            nameLines.forEach((line, i) => ctx.fillText(line, side.x + 28, panelY + 60 + i * 40));

            ctx.fillStyle = side.won ? '#2ddc84' : '#9ea8bb';
            ctx.font = '500 24px system-ui, -apple-system, Segoe UI, sans-serif';
            ctx.fillText(String(side.stat || ''), side.x + 28, panelY + panelH - 34);

            if (side.won) {
                ctx.font = '600 22px system-ui, -apple-system, Segoe UI, sans-serif';
                ctx.textAlign = 'right';
                ctx.fillStyle = '#2ddc84';
                ctx.fillText('WINNER', side.x + panelW - 28, panelY + 44);
                ctx.textAlign = 'left';
            }
        }

        // VS divider
        ctx.fillStyle = '#55607a';
        ctx.font = '700 30px system-ui, -apple-system, Segoe UI, sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('VS', CARD_W / 2, panelY + panelH / 2 + 10);
        ctx.textAlign = 'left';

        // Footer
        ctx.fillStyle = '#78829a';
        ctx.font = '400 22px system-ui, -apple-system, Segoe UI, sans-serif';
        ctx.fillText(String(card.footer || ''), 64, CARD_H - 56);

        if (card.badge) {
            ctx.textAlign = 'right';
            ctx.fillStyle = '#eab308';
            ctx.fillText(String(card.badge), CARD_W - 64, CARD_H - 56);
            ctx.textAlign = 'left';
        }

        const link = document.createElement('a');
        link.download = String(card.fileName || 'polocalcompare-result') + '.png';
        link.href = canvas.toDataURL('image/png');
        link.click();
    };
})();
