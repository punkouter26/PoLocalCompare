/**
 * backdrop.js — the app's one WebGL2 surface: a slow domain-warped gradient behind every page.
 *
 * This reverses the 2026-09-26 prune, which removed a WebGL backdrop, on an explicit request
 * (2026-10-11). What makes it defensible this time is that it never draws while anything is
 * being measured:
 *
 *   1. It stops while the GPU lease is held (window.poGpuLease, util.js) — a browser model is
 *      running WebLLM inference on this tab's WebGPU device.
 *   2. It stops on /arena and /tournament altogether, lease or not. Those are the two routes
 *      that report tok/s, and a remote-vs-remote duel holds no lease while its streaming rate
 *      is still being timed in this tab.
 *   3. It stops when the tab is hidden and under prefers-reduced-motion.
 *
 * "Stops" means the frame loop is cancelled, not throttled: the last frame stays on screen as a
 * still, so the page looks the same and costs nothing. A one-second timer (not a frame loop)
 * notices when the conditions clear, because a Blazor route change is a pushState and fires no
 * event to listen for.
 *
 * It is also cheap when it does run: a third of the viewport's resolution (it is a blurred
 * gradient — CSS upscaling is invisible), 30 fps, low-power adapter, one triangle.
 *
 * Without WebGL2, or if the context is lost, the canvas is removed and the CSS aurora
 * (body::before in app.css) is what shows — which is what every browser had before this.
 *
 * Classic script: it reads window.poGpuLease, which util.js defines just above it.
 */
(() => {
    const SCALE = 1 / 3;
    const FRAME_MS = 1000 / 30;
    /** Routes that measure tok/s. The backdrop holds a still frame on these. */
    const MEASURING = ['/arena', '/tournament'];

    const reducedQuery = window.matchMedia?.('(prefers-reduced-motion: reduce)');

    const canvas = document.createElement('canvas');
    canvas.id = 'po-living-canvas';
    canvas.setAttribute('aria-hidden', 'true');

    const gl = canvas.getContext('webgl2', {
        alpha: true,
        premultipliedAlpha: true,
        antialias: false,
        depth: false,
        stencil: false,
        powerPreference: 'low-power',
    });
    if (!gl) return;

    const VERT = `#version 300 es
        void main() {
            // One oversized triangle covering the viewport; no buffers to bind.
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }`;

    const FRAG = `#version 300 es
        precision highp float;
        uniform vec2 uRes;
        uniform float uTime;
        uniform float uGain;
        uniform vec3 uA;
        uniform vec3 uB;
        uniform vec3 uC;
        out vec4 outColor;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
                       mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
        }

        float fbm(vec2 p) {
            float v = 0.0, a = 0.5;
            for (int i = 0; i < 4; i++) { v += a * noise(p); p = p * 2.02 + vec2(17.3, 9.1); a *= 0.5; }
            return v;
        }

        void main() {
            vec2 p = (gl_FragCoord.xy - 0.5 * uRes) / uRes.y;
            float t = uTime * 0.03;

            // Two rounds of domain warping: the field is bent by itself, which is what makes
            // it read as something flowing rather than as drifting blobs.
            vec2 q = vec2(fbm(p * 1.2 + t), fbm(p * 1.2 - t + 4.7));
            vec2 r = vec2(fbm(p * 1.6 + 2.2 * q + vec2(1.7, 9.2) + t * 0.6),
                          fbm(p * 1.6 + 2.2 * q + vec2(8.3, 2.8) - t * 0.5));
            float f = fbm(p * 1.4 + 2.5 * r);

            vec3 col = mix(uA, uB, smoothstep(0.15, 0.85, r.x));
            col = mix(col, uC, smoothstep(0.30, 0.90, q.y * f * 1.6));

            // Premultiplied: a tint over the page's own background, never an opaque layer.
            float a = uGain * smoothstep(0.25, 0.85, f);
            outColor = vec4(col * a, a);
        }`;

    function compile(type, source) {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        return gl.getShaderParameter(shader, gl.COMPILE_STATUS) ? shader : null;
    }

    const vs = compile(gl.VERTEX_SHADER, VERT);
    const fs = compile(gl.FRAGMENT_SHADER, FRAG);
    if (!vs || !fs) return;

    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) return;

    gl.useProgram(program);
    gl.bindVertexArray(gl.createVertexArray());

    const uniforms = {
        res: gl.getUniformLocation(program, 'uRes'),
        time: gl.getUniformLocation(program, 'uTime'),
        gain: gl.getUniformLocation(program, 'uGain'),
        a: gl.getUniformLocation(program, 'uA'),
        b: gl.getUniformLocation(program, 'uB'),
        c: gl.getUniformLocation(program, 'uC'),
    };

    /** Resolves a colour token to 0–1 RGB through the browser, so any CSS colour syntax works. */
    const probe = document.createElement('span');
    function rgb(tokenName, fallback) {
        probe.style.color = fallback;
        probe.style.color = `var(${tokenName}, ${fallback})`;
        const parts = getComputedStyle(probe).color.match(/[\d.]+/g) ?? [];
        const [r, g, b] = parts.map(Number);
        return [r, g, b].every(Number.isFinite) ? [r / 255, g / 255, b / 255] : [0.3, 0.6, 1];
    }

    /**
     * Palette and strength follow the live theme, including the header toggle's [data-theme]
     * override. The tint is much weaker on a light page: there the layer darkens the background
     * under body text, and muted text must keep its contrast.
     */
    function applyTheme() {
        gl.uniform3fv(uniforms.a, rgb('--accent-green', '#2ddc84'));
        gl.uniform3fv(uniforms.b, rgb('--accent-blue', '#53a6ff'));
        gl.uniform3fv(uniforms.c, rgb('--accent-cyan', '#12b8cf'));
        const [r, g, b] = rgb('--text', '#ffffff');
        const darkTheme = 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.5;
        gl.uniform1f(uniforms.gain, darkTheme ? 0.20 : 0.07);
    }

    function resize() {
        const w = Math.max(64, Math.floor(window.innerWidth * SCALE));
        const h = Math.max(64, Math.floor(window.innerHeight * SCALE));
        if (canvas.width === w && canvas.height === h) return;
        canvas.width = w;
        canvas.height = h;
        gl.viewport(0, 0, w, h);
        gl.uniform2f(uniforms.res, w, h);
    }

    // Accumulated animation time rather than the wall clock, so a pause resumes where it left
    // off instead of jumping.
    let clock = 0;
    function draw() {
        gl.uniform1f(uniforms.time, clock);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    function mayAnimate() {
        if (document.hidden || reducedQuery?.matches) return false;
        if (window.poGpuLease?.busy()) return false;
        const path = window.location.pathname;
        return !MEASURING.some(route => path === route || path.startsWith(route + '/'));
    }

    let raf = 0;
    let last = 0;
    function frame(now) {
        raf = 0;
        if (!mayAnimate()) return;   // the last frame stays up as a still
        if (now - last >= FRAME_MS) {
            clock += Math.min((now - last) / 1000, 0.1);
            last = now;
            draw();
        }
        raf = requestAnimationFrame(frame);
    }

    function wake() {
        if (raf || !canvas.isConnected || !mayAnimate()) return;
        last = performance.now();
        raf = requestAnimationFrame(frame);
    }

    function stop() {
        if (raf) cancelAnimationFrame(raf);
        raf = 0;
    }

    function teardown() {
        stop();
        clearInterval(watch);
        canvas.remove();
        document.documentElement.classList.remove('po-shader');
    }

    function mount() {
        // Reduced motion gets no canvas at all — the static page gradient is the still image.
        if (reducedQuery?.matches) return;
        document.body.prepend(canvas);
        document.body.appendChild(probe);
        probe.hidden = true;
        // Tells app.css the shader is live, so the CSS aurora it replaces is not drawn under it.
        document.documentElement.classList.add('po-shader');
        applyTheme();
        resize();
        draw();
        wake();
    }

    canvas.addEventListener('webglcontextlost', teardown);
    window.addEventListener('resize', () => { resize(); draw(); });
    document.addEventListener('visibilitychange', () => (document.hidden ? stop() : wake()));
    window.poGpuLease?.subscribe(busy => (busy ? stop() : wake()));
    reducedQuery?.addEventListener?.('change', () => (reducedQuery.matches ? teardown() : wake()));
    window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', () => { applyTheme(); draw(); });
    new MutationObserver(() => { applyTheme(); draw(); })
        .observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    // Route changes are pushState and fire nothing, so the conditions are re-checked on a slow
    // timer. This is the only thing that runs while the loop is stopped.
    const watch = setInterval(wake, 1000);

    if (document.body) mount();
    else document.addEventListener('DOMContentLoaded', mount);
})();
