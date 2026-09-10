/**
 * fx.js — Canvas2D particle bursts for the app's two payoff moments.
 *
 * Deliberately NOT a WebGL/WebGPU renderer, and deliberately not a persistent loop.
 *
 * Browser models run WebLLM inference over WebGPU in this same tab, and the tok/s the race
 * reports is measured while that is happening. A render loop stealing GPU would not merely
 * look bad, it would make the number the app exists to report wrong — and slow a browser
 * model's own generation while it is being timed. So the only effects here are one-shot, they
 * run on Canvas2D rather than the 3D pipeline, and they are fired at moments when inference
 * has already finished: a verdict landing and a champion being crowned.
 *
 * Everything self-terminates. The canvas is created on demand, animated for well under a
 * second, and removed — there is no idle cost when nothing is celebrating.
 */

/** Hard ceiling on concurrent bursts, so a fast click-through cannot stack canvases. */
const MAX_ACTIVE = 2;
let active = 0;

function prefersReducedMotion() {
    try {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch {
        return false;
    }
}

/**
 * Reads a design token off the document so particles inherit the live theme — including the
 * user's explicit [data-theme] override, which is why this reads computed style rather than
 * hard-coding a palette.
 */
function token(name, fallback) {
    try {
        const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return value || fallback;
    } catch {
        return fallback;
    }
}

function createOverlay() {
    const canvas = document.createElement('canvas');
    // Fixed and non-interactive: the burst is decoration over whatever is underneath, and must
    // never swallow a click meant for the verdict buttons behind it.
    canvas.className = 'fx-overlay';
    canvas.setAttribute('aria-hidden', 'true');

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.floor(window.innerWidth * dpr);
    canvas.height = Math.floor(window.innerHeight * dpr);

    document.body.appendChild(canvas);

    const ctx = canvas.getContext('2d');
    if (ctx) ctx.scale(dpr, dpr);

    return { canvas, ctx };
}

/**
 * Fires a particle burst from a point.
 *
 * @param {object} options
 * @param {number} options.x        origin, CSS pixels (defaults to viewport centre)
 * @param {number} options.y        origin, CSS pixels
 * @param {number} options.count    particle count
 * @param {number} options.spread   initial speed, px/s
 * @param {string[]} options.colors palette; defaults to the theme's accent
 * @param {number} options.durationMs
 */
export function burst(options = {}) {
    // Respecting reduced-motion by not animating at all, rather than by animating faster.
    // A confetti burst has no non-moving equivalent worth substituting.
    if (prefersReducedMotion()) return;
    if (active >= MAX_ACTIVE) return;
    if (!document.body) return;

    const {
        x = window.innerWidth / 2,
        y = window.innerHeight / 3,
        count = 90,
        spread = 520,
        durationMs = 1400,
    } = options;

    const colors = options.colors && options.colors.length
        ? options.colors
        : [
            token('--accent-green', '#2ddc84'),
            token('--accent-blue', '#53a6ff'),
            token('--accent-yellow', '#eab308'),
            token('--text', '#ffffff'),
        ];

    const overlay = createOverlay();
    const ctx = overlay.ctx;
    if (!ctx) {
        overlay.canvas.remove();
        return;
    }

    active++;

    const gravity = 900;      // px/s², enough that the arc reads as falling confetti
    const drag = 0.86;        // per second; without it everything exits the viewport at once

    const particles = [];
    for (let i = 0; i < count; i++) {
        // Biased upward: a full circle sprays as much into the floor as the air, which reads
        // as a puff rather than a celebration.
        const angle = (-Math.PI / 2) + (Math.random() - 0.5) * Math.PI * 1.1;
        const speed = spread * (0.35 + Math.random() * 0.65);

        particles.push({
            x, y,
            vx: Math.cos(angle) * speed,
            vy: Math.sin(angle) * speed,
            size: 3 + Math.random() * 5,
            spin: (Math.random() - 0.5) * 12,
            rot: Math.random() * Math.PI,
            color: colors[i % colors.length],
        });
    }

    const started = performance.now();
    let previous = started;

    function frame(now) {
        // Real elapsed time rather than a fixed step, so the arc is the same on a 144Hz display
        // as on a 60Hz one. Clamped because a backgrounded tab resumes with a huge delta that
        // would teleport every particle off screen.
        const dt = Math.min((now - previous) / 1000, 0.05);
        previous = now;

        const elapsed = now - started;
        const life = elapsed / durationMs;

        if (life >= 1) {
            overlay.canvas.remove();
            active--;
            return;
        }

        ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
        ctx.globalAlpha = 1 - life * life;   // hold opacity, then fade off quickly at the end

        const decay = Math.pow(drag, dt);

        for (const p of particles) {
            p.vx *= decay;
            p.vy = p.vy * decay + gravity * dt;
            p.x += p.vx * dt;
            p.y += p.vy * dt;
            p.rot += p.spin * dt;

            ctx.save();
            ctx.translate(p.x, p.y);
            ctx.rotate(p.rot);
            ctx.fillStyle = p.color;
            ctx.fillRect(-p.size / 2, -p.size / 2, p.size, p.size * 0.6);
            ctx.restore();
        }

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);
}

/**
 * Burst centred on an element — used for the verdict panel and the champion banner, so the
 * effect originates from the thing being celebrated rather than from the middle of the screen.
 * Falls back to the viewport centre when the selector matches nothing.
 */
export function burstFrom(selector, options = {}) {
    let origin = {};

    try {
        const element = document.querySelector(selector);
        if (element) {
            const rect = element.getBoundingClientRect();
            origin = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
        }
    } catch {
        // Bad selector — fall through to the default centre.
    }

    burst({ ...origin, ...options });
}

// ── 1. Shockwave Ripple ──────────────────────────────────────────────────────

/**
 * High-speed expanding radial displacement shockwave for photo-finishes and impacts.
 */
export function shockwave(options = {}) {
    if (prefersReducedMotion()) return;
    if (active >= MAX_ACTIVE + 2) return;
    if (!document.body) return;

    const {
        x = window.innerWidth / 2,
        y = window.innerHeight / 2,
        maxRadius = Math.min(window.innerWidth, window.innerHeight) * 0.65,
        durationMs = 650,
        color = token('--accent-cyan', '#12b8cf'),
    } = options;

    const overlay = createOverlay();
    const ctx = overlay.ctx;
    if (!ctx) {
        overlay.canvas.remove();
        return;
    }

    active++;
    const started = performance.now();

    function frame(now) {
        const elapsed = now - started;
        const progress = Math.min(elapsed / durationMs, 1);

        if (progress >= 1) {
            overlay.canvas.remove();
            active--;
            return;
        }

        // Ease-out cubic for explosive deceleration
        const eased = 1 - Math.pow(1 - progress, 3);
        const currentRadius = maxRadius * eased;
        const alpha = Math.pow(1 - progress, 1.8);

        ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);

        // Multi-ring chromatic dispersion (cyan inner, white crest, blue outer)
        ctx.save();
        ctx.lineWidth = Math.max(1, (1 - progress) * 8);

        // Outer glow
        ctx.strokeStyle = color;
        ctx.globalAlpha = alpha * 0.45;
        ctx.beginPath();
        ctx.arc(x, y, currentRadius, 0, Math.PI * 2);
        ctx.stroke();

        // Sharp crest ring
        ctx.strokeStyle = '#ffffff';
        ctx.globalAlpha = alpha * 0.85;
        ctx.lineWidth = Math.max(1, (1 - progress) * 3);
        ctx.beginPath();
        ctx.arc(x, y, Math.max(0, currentRadius - 3), 0, Math.PI * 2);
        ctx.stroke();

        ctx.restore();

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);
}

export function shockwaveFrom(selector, options = {}) {
    let origin = {};
    try {
        const el = document.querySelector(selector);
        if (el) {
            const r = el.getBoundingClientRect();
            origin = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
        }
    } catch { }
    shockwave({ ...origin, ...options });
}

// ── 2. 2.5D Shard Shatter ───────────────────────────────────────────────────

/**
 * Explodes angular polygonal glass/light shards from an element on verdict landing.
 */
export function shardShatter(options = {}) {
    if (prefersReducedMotion()) return;
    if (active >= MAX_ACTIVE + 2) return;
    if (!document.body) return;

    const {
        x = window.innerWidth / 2,
        y = window.innerHeight / 2,
        count = 60,
        spread = 600,
        durationMs = 1100,
        colors = [
            token('--accent-cyan', '#12b8cf'),
            token('--accent-blue', '#53a6ff'),
            token('--accent-yellow', '#eab308'),
            '#ffffff'
        ],
    } = options;

    const overlay = createOverlay();
    const ctx = overlay.ctx;
    if (!ctx) {
        overlay.canvas.remove();
        return;
    }

    active++;

    const shards = [];
    for (let i = 0; i < count; i++) {
        const angle = Math.random() * Math.PI * 2;
        const speed = spread * (0.3 + Math.random() * 0.7);
        shards.push({
            x, y,
            vx: Math.cos(angle) * speed,
            vy: Math.sin(angle) * speed - 150, // initial upward lift
            rotX: Math.random() * Math.PI,
            rotY: Math.random() * Math.PI,
            spinX: (Math.random() - 0.5) * 14,
            spinY: (Math.random() - 0.5) * 14,
            size: 6 + Math.random() * 12,
            aspect: 0.3 + Math.random() * 0.6,
            color: colors[i % colors.length]
        });
    }

    const started = performance.now();
    let prev = started;

    function frame(now) {
        const dt = Math.min((now - prev) / 1000, 0.05);
        prev = now;

        const elapsed = now - started;
        const life = elapsed / durationMs;

        if (life >= 1) {
            overlay.canvas.remove();
            active--;
            return;
        }

        ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
        ctx.globalAlpha = Math.max(0, 1 - Math.pow(life, 2));

        const gravity = 1100;
        const drag = 0.88;
        const decay = Math.pow(drag, dt);

        for (const s of shards) {
            s.vx *= decay;
            s.vy = s.vy * decay + gravity * dt;
            s.x += s.vx * dt;
            s.y += s.vy * dt;
            s.rotX += s.spinX * dt;
            s.rotY += s.spinY * dt;

            const scaleX = Math.cos(s.rotX);
            const scaleY = Math.sin(s.rotY);

            ctx.save();
            ctx.translate(s.x, s.y);
            ctx.scale(scaleX, scaleY);
            ctx.fillStyle = s.color;
            ctx.beginPath();
            ctx.moveTo(-s.size, -s.size * s.aspect);
            ctx.lineTo(s.size, 0);
            ctx.lineTo(-s.size * 0.3, s.size * s.aspect);
            ctx.closePath();
            ctx.fill();
            ctx.restore();
        }

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);
}

export function shardShatterFrom(selector, options = {}) {
    let origin = {};
    try {
        const el = document.querySelector(selector);
        if (el) {
            const r = el.getBoundingClientRect();
            origin = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
        }
    } catch { }
    shardShatter({ ...origin, ...options });
}

// ── 3. 3D Tumbling Champion Pyrotechnics ──────────────────────────────────────

/**
 * Multi-stage grand celebration with 3D tumbling ribbon confetti and golden embers.
 */
export function championPyrotechnics(options = {}) {
    if (prefersReducedMotion()) return;
    if (active >= MAX_ACTIVE + 2) return;
    if (!document.body) return;

    const {
        x = window.innerWidth / 2,
        y = window.innerHeight * 0.35,
        count = 180,
        durationMs = 2800,
    } = options;

    const overlay = createOverlay();
    const ctx = overlay.ctx;
    if (!ctx) {
        overlay.canvas.remove();
        return;
    }

    active++;

    const goldPalette = ['#ffd700', '#ffb700', '#ffe57f', '#2ddc84', '#53a6ff', '#ffffff'];
    const confetti = [];

    for (let i = 0; i < count; i++) {
        const angle = (-Math.PI / 2) + (Math.random() - 0.5) * Math.PI * 1.6;
        const speed = 400 + Math.random() * 650;
        confetti.push({
            x, y,
            vx: Math.cos(angle) * speed,
            vy: Math.sin(angle) * speed,
            rot: Math.random() * Math.PI * 2,
            rotSpeed: (Math.random() - 0.5) * 10,
            tilt: Math.random() * Math.PI,
            tiltSpeed: 4 + Math.random() * 8,
            width: 7 + Math.random() * 9,
            height: 14 + Math.random() * 16,
            color: goldPalette[i % goldPalette.length],
            isRibbon: Math.random() > 0.4
        });
    }

    const started = performance.now();
    let prev = started;

    function frame(now) {
        const dt = Math.min((now - prev) / 1000, 0.05);
        prev = now;

        const elapsed = now - started;
        const life = elapsed / durationMs;

        if (life >= 1) {
            overlay.canvas.remove();
            active--;
            return;
        }

        ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
        ctx.globalAlpha = Math.max(0, 1 - Math.pow(life, 3));

        const gravity = 750;
        const drag = 0.93;
        const decay = Math.pow(drag, dt);

        for (const c of confetti) {
            c.vx *= decay;
            c.vy = c.vy * decay + gravity * dt;
            c.x += c.vx * dt;
            c.y += c.vy * dt;
            c.rot += c.rotSpeed * dt;
            c.tilt += c.tiltSpeed * dt;

            // Pseudo 3D perspective tumbling
            const scaleY = Math.sin(c.tilt);

            ctx.save();
            ctx.translate(c.x, c.y);
            ctx.rotate(c.rot);
            ctx.scale(1, scaleY);
            ctx.fillStyle = c.color;

            if (c.isRibbon) {
                ctx.fillRect(-c.width / 2, -c.height / 2, c.width, c.height);
            } else {
                ctx.beginPath();
                ctx.arc(0, 0, c.width * 0.4, 0, Math.PI * 2);
                ctx.fill();
            }
            ctx.restore();
        }

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);
}

export function championPyrotechnicsFrom(selector) {
    let origin = {};
    try {
        const el = document.querySelector(selector);
        if (el) {
            const r = el.getBoundingClientRect();
            origin = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
        }
    } catch { }
    championPyrotechnics(origin);
}

// ── 4. Elo Kinetic Mote Transfer ─────────────────────────────────────────────

/**
 * Transfers glowing energy motes from the loser to the winner's Elo badge.
 */
export function moteTransfer(fromSelector, toSelector, count = 24) {
    if (prefersReducedMotion()) return;
    if (!document.body) return;

    let fromX = window.innerWidth * 0.25, fromY = window.innerHeight * 0.5;
    let toX = window.innerWidth * 0.75, toY = window.innerHeight * 0.5;

    try {
        const fromEl = document.querySelector(fromSelector);
        const toEl = document.querySelector(toSelector);
        if (fromEl) {
            const r = fromEl.getBoundingClientRect();
            fromX = r.left + r.width / 2;
            fromY = r.top + r.height / 2;
        }
        if (toEl) {
            const r = toEl.getBoundingClientRect();
            toX = r.left + r.width / 2;
            toY = r.top + r.height / 2;
        }
    } catch { }

    const overlay = createOverlay();
    const ctx = overlay.ctx;
    if (!ctx) {
        overlay.canvas.remove();
        return;
    }

    active++;
    const durationMs = 850;

    const motes = [];
    for (let i = 0; i < count; i++) {
        // Curved trajectory with arched control point
        const midX = (fromX + toX) / 2 + (Math.random() - 0.5) * 160;
        const midY = Math.min(fromY, toY) - 80 - Math.random() * 120;
        motes.push({
            p0: { x: fromX + (Math.random() - 0.5) * 40, y: fromY + (Math.random() - 0.5) * 40 },
            p1: { x: midX, y: midY },
            p2: { x: toX, y: toY },
            delay: i * 22,
            size: 3 + Math.random() * 4,
            color: i % 2 === 0 ? token('--accent-green', '#2ddc84') : token('--accent-yellow', '#eab308'),
        });
    }

    const started = performance.now();

    function frame(now) {
        const elapsed = now - started;

        let allDone = true;
        ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);

        for (const m of motes) {
            if (elapsed < m.delay) {
                allDone = false;
                continue;
            }

            const t = Math.min(1, (elapsed - m.delay) / (durationMs - m.delay));
            if (t < 1) allDone = false;

            // Quadratic bezier position
            const inv = 1 - t;
            const x = inv * inv * m.p0.x + 2 * inv * t * m.p1.x + t * t * m.p2.x;
            const y = inv * inv * m.p0.y + 2 * inv * t * m.p1.y + t * t * m.p2.y;

            ctx.save();
            ctx.fillStyle = m.color;
            ctx.shadowColor = m.color;
            ctx.shadowBlur = 8;
            ctx.beginPath();
            ctx.arc(x, y, m.size * (1 - t * 0.3), 0, Math.PI * 2);
            ctx.fill();
            ctx.restore();
        }

        if (allDone || elapsed > durationMs + 100) {
            overlay.canvas.remove();
            active--;
            return;
        }

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);
}

// ── 5. Reactive Living Atmosphere Background ─────────────────────────────────

let livingCanvas = null;
let livingCtx = null;
let livingState = 'idle'; // 'idle' | 'battle' | 'victory'
let livingPaused = false;
let livingAnimId = null;

/**
 * Initializes the subtle ambient reactive canvas behind MainLayout.
 * Auto-pauses during local WebGPU inference so it never steals GPU compute!
 */
export function initLivingCanvas(canvasId = 'po-living-canvas') {
    if (prefersReducedMotion()) return;
    livingCanvas = document.getElementById(canvasId);
    if (!livingCanvas) return;

    livingCtx = livingCanvas.getContext('2d');
    if (!livingCtx) return;

    const resize = () => {
        if (!livingCanvas) return;
        livingCanvas.width = window.innerWidth;
        livingCanvas.height = window.innerHeight;
    };
    window.addEventListener('resize', resize);
    resize();

    let step = 0;

    function render() {
        if (!livingPaused && livingCtx && livingCanvas) {
            step += (livingState === 'battle' ? 0.015 : 0.005);

            const w = livingCanvas.width;
            const h = livingCanvas.height;

            livingCtx.clearRect(0, 0, w, h);

            // Orbiting soft plasma color nodes
            const node1X = w * (0.25 + 0.15 * Math.sin(step * 0.8));
            const node1Y = h * (0.2 + 0.1 * Math.cos(step * 0.6));
            const node2X = w * (0.75 + 0.12 * Math.cos(step * 0.7));
            const node2Y = h * (0.3 + 0.15 * Math.sin(step * 0.9));

            const radius = Math.max(w, h) * 0.45;

            // Node 1: Left color
            const grad1 = livingCtx.createRadialGradient(node1X, node1Y, 0, node1X, node1Y, radius);
            const color1 = livingState === 'victory'
                ? 'rgba(234, 179, 8, 0.08)'
                : 'rgba(18, 184, 207, 0.07)';
            grad1.addColorStop(0, color1);
            grad1.addColorStop(1, 'transparent');

            // Node 2: Right color
            const grad2 = livingCtx.createRadialGradient(node2X, node2Y, 0, node2X, node2Y, radius);
            const color2 = livingState === 'victory'
                ? 'rgba(255, 215, 0, 0.06)'
                : livingState === 'battle'
                    ? 'rgba(45, 220, 132, 0.08)'
                    : 'rgba(83, 166, 255, 0.05)';
            grad2.addColorStop(0, color2);
            grad2.addColorStop(1, 'transparent');

            livingCtx.fillStyle = grad1;
            livingCtx.fillRect(0, 0, w, h);

            livingCtx.fillStyle = grad2;
            livingCtx.fillRect(0, 0, w, h);
        }

        livingAnimId = requestAnimationFrame(render);
    }

    if (!livingAnimId) render();
}

/** Sets the living atmosphere state ('idle', 'battle', 'victory') */
export function setLivingState(state) {
    livingState = state;
}

/**
 * CRITICAL WebGPU Invariant: Pauses living canvas when local WebLLM inference begins
 * to safeguard 100% of GPU compute pipelines for token generation.
 */
export function pauseLiving(paused) {
    livingPaused = !!paused;
    if (livingPaused && livingCtx && livingCanvas) {
        livingCtx.clearRect(0, 0, livingCanvas.width, livingCanvas.height);
    }
}

