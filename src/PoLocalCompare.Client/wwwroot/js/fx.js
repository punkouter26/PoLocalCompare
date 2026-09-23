/**
 * fx.js — the app's canvas effects: one-shot payoff bursts, the photo-finish strip, and the
 * shader backdrop behind idle pages.
 *
 * Browser models run WebLLM inference over WebGPU in this same tab, and the tok/s the race
 * reports is measured while that is happening. A render loop stealing GPU would not merely
 * look bad, it would make the number the app exists to report wrong — and slow a browser
 * model's own generation while it is being timed. Two rules follow from that:
 *
 *   1. Every effect here checks the GPU lease (window.poGpuLease, util.js) and does nothing
 *      while it is held. webllm-interop.js holds it for exactly the life of a WebLLM worker.
 *   2. The one-shots are Canvas2D, run for well under three seconds and remove their canvas.
 *      The only continuous effect — the backdrop — is WebGL2 at quarter resolution, capped at
 *      30 fps, only on routes where nothing can be inferring, and it STOPS (no idle rAF) the
 *      moment the lease is taken, the tab is hidden or the route changes.
 */

/** Hard ceiling on concurrent bursts, so a fast click-through cannot stack canvases. */
const MAX_ACTIVE = 2;
let active = 0;

/** True while a WebLLM worker holds the GPU — see util.js. */
function gpuBusy() {
    try {
        return !!window.poGpuLease?.busy();
    } catch {
        return false;
    }
}

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
    if (gpuBusy()) return;
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

        const elapsed = Math.max(0, now - started);
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
    if (gpuBusy()) return;
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
        // Clamped: rAF's timestamp is the frame's START, which can precede the
        // performance.now() taken above, so the first frame's elapsed is often slightly
        // negative. Here that made the eased radius negative, arc() threw, and the throw
        // skipped the cleanup — leaking the overlay canvas and the active count.
        const elapsed = Math.max(0, now - started);
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
    if (gpuBusy()) return;
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

        const elapsed = Math.max(0, now - started);
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
    if (gpuBusy()) return;
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

        const elapsed = Math.max(0, now - started);
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
    if (gpuBusy()) return;
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
        const elapsed = Math.max(0, now - started);

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


// ── 5. Photo finish ──────────────────────────────────────────────────────────

/**
 * A slit-scan strip of the race, shown when both models cross the line almost together.
 *
 * Each lane is the side's tok/s history laid out left to right, one column per sample, with
 * height and brightness tracking pace — the same data the race sparklines drew, read the way a
 * finish-line camera reads a race. Fired from the Arena on DuelComplete, so inference is over
 * by construction; the lease check still runs, because a tournament tab can start the next
 * browser match while a duel page is open.
 *
 * Decorative: the Arena states the margin as text in a status region, so this canvas is
 * aria-hidden like every other overlay here.
 *
 * @param {object} data
 * @param {{name: string, history: number[]}} data.left
 * @param {{name: string, history: number[]}} data.right
 * @param {string} data.winner  display name of whoever crossed first
 * @param {number} data.marginMs
 */
export function photoFinish(data) {
    if (prefersReducedMotion() || gpuBusy()) return;
    if (!document.body || !data) return;

    const overlay = createOverlay();
    const ctx = overlay.ctx;
    if (!ctx) {
        overlay.canvas.remove();
        return;
    }

    active++;

    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const w = Math.min(640, vw - 32);
    const laneH = 46;
    const h = laneH * 2 + 96;
    const x0 = (vw - w) / 2;
    const y0 = Math.max(24, vh * 0.3 - h / 2);

    const lanes = [
        { ...data.left, color: token('--accent-cyan', '#12b8cf') },
        { ...data.right, color: token('--accent-purple', '#7d96ff') },
    ];
    const text = token('--text', '#e6edf3');
    const surface = token('--surface-1', '#131b2d');

    const develop = 520;   // the strip "develops" left to right
    const hold = 1700;
    const fade = 420;
    const total = develop + hold + fade;
    const started = performance.now();

    function drawLane(lane, y, reveal) {
        const history = lane.history && lane.history.length ? lane.history : [0];
        const peak = Math.max(1, ...history);
        const cols = history.length;
        const colW = (w - 32) / cols;
        const limit = Math.ceil(cols * reveal);

        ctx.fillStyle = 'rgba(0,0,0,0.35)';
        ctx.fillRect(x0 + 16, y, w - 32, laneH);

        for (let i = 0; i < limit; i++) {
            const k = Math.max(0.08, history[i] / peak);
            ctx.globalAlpha = 0.25 + k * 0.75;
            ctx.fillStyle = lane.color;
            // Taller columns for faster samples: the strip reads as a waveform of pace.
            const colH = laneH * (0.35 + k * 0.65);
            ctx.fillRect(x0 + 16 + i * colW, y + (laneH - colH) / 2, Math.max(1, colW - 1), colH);
        }
        ctx.globalAlpha = 1;

        ctx.fillStyle = text;
        ctx.font = '600 12px system-ui, sans-serif';
        ctx.fillText(lane.name ?? '', x0 + 20, y - 6);
    }

    function frame(now) {
        const elapsed = Math.max(0, now - started);
        if (elapsed >= total) {
            overlay.canvas.remove();
            active--;
            return;
        }

        const reveal = Math.min(1, elapsed / develop);
        const alpha = elapsed > develop + hold
            ? 1 - (elapsed - develop - hold) / fade
            : Math.min(1, elapsed / 140);

        ctx.clearRect(0, 0, vw, vh);

        ctx.save();
        ctx.fillStyle = surface;
        ctx.globalAlpha = alpha * 0.94;
        ctx.beginPath();
        if (ctx.roundRect) ctx.roundRect(x0, y0, w, h, 14);
        else ctx.rect(x0, y0, w, h);
        ctx.fill();
        ctx.restore();

        ctx.save();
        ctx.globalAlpha = alpha;
        ctx.fillStyle = text;
        ctx.font = '800 13px system-ui, sans-serif';
        ctx.fillText('PHOTO FINISH', x0 + 16, y0 + 24);

        drawLane(lanes[0], y0 + 50, reveal);
        ctx.globalAlpha = alpha;
        drawLane(lanes[1], y0 + 50 + laneH + 22, reveal);
        ctx.globalAlpha = alpha;

        // The finish line flashes once the strip has fully developed.
        if (reveal >= 1) {
            const flash = Math.max(0, 1 - (elapsed - develop) / 380);
            ctx.fillStyle = '#ffffff';
            ctx.globalAlpha = alpha * (0.55 + flash * 0.45);
            ctx.fillRect(x0 + w - 18, y0 + 40, 3, laneH * 2 + 36);
            ctx.globalAlpha = alpha;

            ctx.fillStyle = text;
            ctx.font = '700 13px system-ui, sans-serif';
            ctx.textAlign = 'right';
            ctx.fillText(`${data.winner} by ${(data.marginMs / 1000).toFixed(2)} s`, x0 + w - 16, y0 + 24);
            ctx.textAlign = 'left';
        }
        ctx.restore();

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);
}

// ── 6. Shader backdrop ───────────────────────────────────────────────────────

/**
 * Domain-warped gradient behind idle pages, in the theme's own accent tokens.
 *
 * Raw WebGL2, not Three.js: it is one fragment shader, and a library would be a large download
 * that makes an always-on render loop the easy thing to write. Its cost is bounded three ways —
 * quarter resolution (CSS stretches it; the softness is the look), a 30 fps cap, and four fbm
 * octaves — and it only runs while ALL of these hold:
 *
 *   - the route cannot be inferring (not /arena, not /tournament unless a champion is crowned);
 *   - the GPU lease is free;
 *   - the tab is visible;
 *   - reduced motion and increased contrast are both off.
 *
 * When any of them stops holding, the loop is cancelled outright rather than spinning with the
 * drawing skipped. With no WebGL2 it does nothing and the CSS aurora (body::before) carries
 * the page alone, as it always did.
 */

const LIVING_BLOCKED_ROUTES = ['/arena', '/tournament', '/diag'];

const living = {
    canvas: null,
    gl: null,
    uniforms: null,
    rafId: 0,
    lastFrame: 0,
    started: 0,
    state: 'idle',      // 'idle' | 'victory'
    route: '/',
    palette: null,
    lost: false,
};

const VERT = `#version 300 es
in vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }`;

const FRAG = `#version 300 es
precision mediump float;
uniform vec2 uRes;
uniform float uTime;
uniform vec3 uA;
uniform vec3 uB;
uniform vec3 uC;
uniform float uAlpha;
out vec4 outColor;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++) {
        v += a * noise(p);
        p = p * 2.03 + vec2(1.7, 9.2);
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / uRes;
    vec2 p = uv * vec2(uRes.x / uRes.y, 1.0) * 1.6;
    float t = uTime * 0.04;

    // Two levels of domain warping: the flow folds back on itself instead of scrolling.
    vec2 q = vec2(fbm(p + t), fbm(p + vec2(5.2, 1.3) - t));
    vec2 r = vec2(fbm(p + 3.0 * q + vec2(1.7, 9.2) + t * 1.3), fbm(p + 3.0 * q + vec2(8.3, 2.8) - t));
    float f = fbm(p + 3.0 * r);

    vec3 col = mix(uA, uB, clamp(f * f * 2.0, 0.0, 1.0));
    col = mix(col, uC, clamp(length(q) * 0.6, 0.0, 1.0));

    float a = uAlpha * smoothstep(0.2, 0.95, f) * (0.55 + 0.45 * uv.y);
    outColor = vec4(col * a, a);   // premultiplied: the canvas composites over the page
}`;

function hexToRgb(value, fallback) {
    const hex = (value || '').replace('#', '').trim();
    if (!/^[0-9a-f]{6}$/i.test(hex)) return fallback;
    return [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16) / 255);
}

function isDarkTheme() {
    const forced = document.documentElement.dataset.theme;
    if (forced === 'dark') return true;
    if (forced === 'light') return false;
    try {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    } catch {
        return true;
    }
}

function readPalette() {
    const victory = living.state === 'victory';
    return {
        a: hexToRgb(token(victory ? '--accent-yellow' : '--accent-cyan', '#12b8cf'), [0.07, 0.72, 0.81]),
        b: hexToRgb(token(victory ? '--accent-yellow' : '--accent-blue', '#53a6ff'), [0.33, 0.65, 1]),
        c: hexToRgb(token(victory ? '--accent-green' : '--accent-purple', '#7d96ff'), [0.49, 0.59, 1]),
        // Light pages get a fainter wash: the same alpha over white reads as a stain.
        alpha: isDarkTheme() ? 0.22 : 0.10,
    };
}

function livingAllowed() {
    if (!living.gl || living.lost) return false;
    if (prefersReducedMotion() || gpuBusy() || document.hidden) return false;
    try {
        if (window.matchMedia('(prefers-contrast: more)').matches) return false;
    } catch {
        // No matchMedia: treat as the default preference.
    }
    const blocked = LIVING_BLOCKED_ROUTES.some(r => living.route === r || living.route.startsWith(r + '/'));
    // A crowned champion is the one tournament moment with nothing left to infer.
    return !blocked || living.state === 'victory';
}

function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        gl.deleteShader(shader);
        return null;
    }
    return shader;
}

function resizeLiving() {
    const c = living.canvas;
    if (!c) return;
    c.width = Math.max(1, Math.floor(window.innerWidth / 4));
    c.height = Math.max(1, Math.floor(window.innerHeight / 4));
    living.gl?.viewport(0, 0, c.width, c.height);
}

function clearLiving() {
    const gl = living.gl;
    if (!gl || living.lost) return;
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
}

function renderLiving(now) {
    living.rafId = 0;
    if (!livingAllowed()) {
        clearLiving();
        return;
    }

    living.rafId = requestAnimationFrame(renderLiving);
    if (now - living.lastFrame < 33) return;   // 30 fps cap
    living.lastFrame = now;

    const { gl, uniforms, canvas } = living;
    const pal = living.palette ?? (living.palette = readPalette());
    gl.uniform2f(uniforms.res, canvas.width, canvas.height);
    gl.uniform1f(uniforms.time, (now - living.started) / 1000);
    gl.uniform3fv(uniforms.a, pal.a);
    gl.uniform3fv(uniforms.b, pal.b);
    gl.uniform3fv(uniforms.c, pal.c);
    gl.uniform1f(uniforms.alpha, pal.alpha);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
}

/** Starts the loop if everything allows it; cancels it — no idle rAF — if anything does not. */
function syncLiving() {
    if (livingAllowed()) {
        if (!living.rafId) living.rafId = requestAnimationFrame(renderLiving);
    } else if (living.rafId) {
        cancelAnimationFrame(living.rafId);
        living.rafId = 0;
        clearLiving();
    }
}

export function initLivingCanvas(canvasId = 'po-living-canvas') {
    if (living.canvas) return;
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const gl = canvas.getContext('webgl2', {
        alpha: true,
        premultipliedAlpha: true,
        antialias: false,
        depth: false,
        stencil: false,
        // Prefer the integrated GPU on dual-GPU laptops, leaving the discrete one to WebLLM.
        powerPreference: 'low-power',
    });
    if (!gl) return;

    const vs = compile(gl, gl.VERTEX_SHADER, VERT);
    const fs = compile(gl, gl.FRAGMENT_SHADER, FRAG);
    if (!vs || !fs) return;

    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) return;
    gl.useProgram(program);

    // One oversized triangle covers the viewport with no index buffer.
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(program, 'aPos');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

    Object.assign(living, {
        canvas,
        gl,
        started: performance.now(),
        route: location.pathname.toLowerCase(),
        uniforms: {
            res: gl.getUniformLocation(program, 'uRes'),
            time: gl.getUniformLocation(program, 'uTime'),
            a: gl.getUniformLocation(program, 'uA'),
            b: gl.getUniformLocation(program, 'uB'),
            c: gl.getUniformLocation(program, 'uC'),
            alpha: gl.getUniformLocation(program, 'uAlpha'),
        },
    });

    canvas.addEventListener('webglcontextlost', e => {
        e.preventDefault();
        living.lost = true;
        syncLiving();
    });

    resizeLiving();
    window.addEventListener('resize', resizeLiving);
    document.addEventListener('visibilitychange', syncLiving);
    window.poGpuLease?.subscribe(syncLiving);

    // Re-read the palette when the theme flips, from the header toggle or from the OS.
    const repalette = () => { living.palette = null; };
    new MutationObserver(repalette).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    try {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', repalette);
    } catch {
        // Older engines without MediaQueryList events: the palette updates on the next navigation.
    }

    syncLiving();
}

/** 'idle' or 'victory'. A crowned champion switches the wash to gold and unblocks /tournament. */
export function setLivingState(state) {
    living.state = state === 'victory' ? 'victory' : 'idle';
    living.palette = null;
    syncLiving();
}

/** Called by MainLayout on every navigation. A route change also ends a victory wash. */
export function setLivingRoute(path) {
    living.route = (path || '/').split(/[?#]/)[0].toLowerCase() || '/';
    if (living.state === 'victory') {
        living.state = 'idle';
        living.palette = null;
    }
    syncLiving();
}
