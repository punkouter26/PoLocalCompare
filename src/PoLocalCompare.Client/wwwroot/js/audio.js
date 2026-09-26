/**
 * audio.js — three synthesised Web Audio cues: a UI tick, a verdict, and a tie.
 *
 * Every sound is SYNTHESISED at play time. There are no audio assets, deliberately: an earlier
 * version fetched two WAV files that were 44-byte stubs and played silence for as long as they
 * existed. Synthesis means there is nothing that can be present-but-empty.
 *
 * It runs on the browser's audio thread, so it never contends with the WebGPU device WebLLM
 * is running inference on and cannot skew the tok/s figure.
 */

let ctx = null;
let master = null;
let muted = false;

const MUTE_KEY = 'polocalcompare.muted';

/** Ceiling on the master bus. Cues are mixed well below unity so two overlapping never clip. */
const MASTER_GAIN = 0.28;

/**
 * Browsers refuse to start an AudioContext outside a user gesture, and a context created
 * before one starts 'suspended'. Every cue routes through here, so the first sound that
 * follows a click resumes it rather than silently doing nothing.
 */
function ensureCtx() {
    if (ctx) {
        if (ctx.state === 'suspended') ctx.resume().catch(() => { });
        return ctx;
    }

    try {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return null;
        ctx = new Ctor();
        master = ctx.createGain();
        master.gain.value = muted ? 0 : MASTER_GAIN;
        master.connect(ctx.destination);
    } catch {
        ctx = null;
        master = null;
    }
    return ctx;
}

try {
    muted = window.localStorage.getItem(MUTE_KEY) === 'true';
} catch {
    // Storage blocked (private mode). Default to audible.
}

export function isMuted() {
    return muted;
}

export function setMuted(value) {
    muted = !!value;
    try {
        window.localStorage.setItem(MUTE_KEY, muted ? 'true' : 'false');
    } catch {
        // Non-fatal: the setting simply will not survive a reload.
    }
    if (master && ctx) master.gain.setTargetAtTime(muted ? 0 : MASTER_GAIN, ctx.currentTime, 0.01);
    return muted;
}

/**
 * One shaped note. setTargetAtTime for the tail so the decay sounds exponential — a linear
 * fade reads as a synthetic "cut" rather than a note ending.
 */
function tone({ freq, type = 'sine', at = 0, dur = 0.3, gain = 0.5 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const t0 = audio.currentTime + at;
    const osc = audio.createOscillator();
    const amp = audio.createGain();

    osc.type = type;
    osc.frequency.setValueAtTime(freq, t0);

    // 8ms attack: fast enough to feel instant, slow enough to avoid a click transient.
    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(gain, t0 + 0.008);
    amp.gain.setTargetAtTime(0.0001, t0 + 0.008, dur / 3);

    osc.connect(amp);
    amp.connect(master);
    osc.start(t0);
    osc.stop(t0 + dur + 0.1);
    // Oscillators are one-shot; releasing the graph keeps a long session from accumulating nodes.
    osc.onended = () => { try { amp.disconnect(); } catch { } };
}

/** Verdict recorded — a bright major arpeggio (C6 E6 G6 C7) over a quiet fifth. */
export function playSuccess() {
    [1046.5, 1318.5, 1568.0, 2093.0].forEach((freq, i) =>
        tone({ freq, type: 'triangle', at: i * 0.065, dur: 0.34, gain: 0.30 }));
    tone({ freq: 523.25, type: 'sine', at: 0, dur: 0.5, gain: 0.16 });
}

/** A judged draw — deliberately unresolved, neither up nor down. */
export function playTie() {
    tone({ freq: 587.33, type: 'triangle', at: 0, dur: 0.4, gain: 0.24 });
    tone({ freq: 587.33, type: 'triangle', at: 0.16, dur: 0.45, gain: 0.20 });
}

/** UI tick for a click. Very short and quiet — it fires often. */
export function playTick() {
    tone({ freq: 1320, type: 'square', at: 0, dur: 0.035, gain: 0.07 });
}
