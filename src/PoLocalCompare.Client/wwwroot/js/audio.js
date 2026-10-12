/**
 * audio.js — synthesised Web Audio cues: a UI tick, a verdict (panned to the winner's side), a
 * tie, the closing seconds of the judge's window, and an opt-in audible token race.
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
function tone({ freq, type = 'sine', at = 0, dur = 0.3, gain = 0.5, pan = 0 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    // Stereo position, -1 (left) to 1 (right). A centred note skips the node entirely.
    let out = master;
    let panner = null;
    if (pan && audio.createStereoPanner) {
        panner = audio.createStereoPanner();
        panner.pan.value = Math.max(-1, Math.min(1, pan));
        panner.connect(master);
        out = panner;
    }

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
    amp.connect(out);
    osc.start(t0);
    osc.stop(t0 + dur + 0.1);
    // Oscillators are one-shot; releasing the graph keeps a long session from accumulating nodes.
    osc.onended = () => { try { amp.disconnect(); panner?.disconnect(); } catch { } };
}

/**
 * Verdict recorded — a bright major arpeggio (C6 E6 G6 C7) over a quiet fifth.
 *
 * @param {number} [pan] -1…1: the arpeggio comes from the side that won. The fifth under it
 *   stays centred so the cue still sounds like one event on a single speaker.
 */
export function playSuccess(pan = 0) {
    [1046.5, 1318.5, 1568.0, 2093.0].forEach((freq, i) =>
        tone({ freq, type: 'triangle', at: i * 0.065, dur: 0.34, gain: 0.30, pan }));
    tone({ freq: 523.25, type: 'sine', at: 0, dur: 0.5, gain: 0.16 });
}

/**
 * The last seconds of the judge's grace window: one note per second, rising, so the window
 * closing is audible without looking at the clock.
 *
 * @param {number} remaining seconds left (3, 2, 1)
 */
export function playCountdown(remaining) {
    const step = Math.max(0, 3 - Math.max(1, Math.min(3, remaining)));
    tone({ freq: 659.25 * Math.pow(2, (step * 2) / 12), type: 'triangle', at: 0, dur: 0.14, gain: 0.16 });
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

// ── The audible race ─────────────────────────────────────────────────────────
//
// While a duel streams, each side ticks in its own ear at a rate and pitch that follow its
// tok/s, so which model is ahead is something you can hear. OFF by default and stored
// separately from mute: it is continuous sound for as long as a duel runs, which is a thing to
// opt into rather than to discover.
//
// Scheduling is a 50 ms timer that only decides WHEN to call tone(); the notes themselves are
// rendered on the audio thread like every other cue. Nothing here draws, so nothing here can
// touch the GPU a browser model is generating on or skew the figure being sonified.

const RACE_KEY = 'polocalcompare.raceAudio';
/** One tick per this many tokens — per token would be a buzz at any real generation speed. */
const TOKENS_PER_TICK = 4;
/** Ceiling per side, ticks/s. Above this the rate stops reading as a rhythm. */
const MAX_TICKS = 9;
/** The Arena pushes rates a few times a second; this long without one means it has gone away. */
const RACE_SILENCE_MS = 2000;

let raceOn = false;
try {
    raceOn = window.localStorage.getItem(RACE_KEY) === 'true';
} catch {
    // Storage blocked: stays off.
}

const race = { timer: 0, lastPush: 0, lastStep: 0, rate: [0, 0], phase: [0, 0] };

export function isRaceOn() {
    return raceOn;
}

export function setRaceOn(value) {
    raceOn = !!value;
    try {
        window.localStorage.setItem(RACE_KEY, raceOn ? 'true' : 'false');
    } catch {
        // Non-fatal: the setting simply will not survive a reload.
    }
    if (!raceOn) raceStop();
    return raceOn;
}

function raceStep() {
    const now = performance.now();
    if (now - race.lastPush > RACE_SILENCE_MS) {
        raceStop();
        return;
    }
    const dt = Math.min((now - race.lastStep) / 1000, 0.2);
    race.lastStep = now;

    for (let side = 0; side < 2; side++) {
        const tps = race.rate[side];
        if (tps <= 0) continue;
        race.phase[side] += Math.min(tps / TOKENS_PER_TICK, MAX_TICKS) * dt;
        if (race.phase[side] < 1) continue;
        race.phase[side] = 0;
        tone({
            // Faster reads as higher. The sides sit a fourth apart so that two models at the
            // same speed are still two voices rather than one louder one.
            freq: (side === 0 ? 330 : 440) + Math.min(tps, 150) * 3,
            type: 'sine',
            dur: 0.05,
            gain: 0.05,
            pan: side === 0 ? -0.75 : 0.75,
        });
    }
}

/**
 * Sets each side's current tok/s; 0 silences a side (not started, or finished). Starts the
 * race voice if it is not running. A no-op unless the listener has switched race sounds on.
 */
export function raceUpdate(leftTps, rightTps) {
    if (!raceOn || muted) return;
    race.rate[0] = Number(leftTps) || 0;
    race.rate[1] = Number(rightTps) || 0;
    race.lastPush = performance.now();
    if (!race.timer) {
        race.lastStep = race.lastPush;
        race.timer = window.setInterval(raceStep, 50);
    }
}

export function raceStop() {
    if (race.timer) window.clearInterval(race.timer);
    race.timer = 0;
    race.rate = [0, 0];
    race.phase = [0, 0];
}
