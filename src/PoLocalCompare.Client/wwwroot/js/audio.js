/**
 * audio.js — programmatic Web Audio cues for PoLocalCompare.
 *
 * Every sound here is SYNTHESISED at play time. There are no audio assets, deliberately:
 * the previous version fetched /audio/snare-roll.wav and /audio/success.wav, both of which
 * were 44-byte stubs — a RIFF header with a zero-length data chunk. They decoded to an empty
 * buffer and played silence, so the app has been mute since those cues were added. Synthesis
 * removes the asset dependency that made that failure invisible: there is nothing to ship,
 * nothing to 404, and nothing that can be present-but-empty.
 *
 * Cost is a few hundred bytes of oscillator graph per cue, torn down when it finishes. All of
 * it runs on the browser's audio thread, which is why this is safe to use during a duel: it
 * does not contend with the WebGPU device WebLLM is running inference on, and it cannot skew
 * the tok/s figure or a MaxSeconds challenge budget the way a GPU render loop would.
 */

let ctx = null;
let master = null;
let muted = false;

const MUTE_KEY = 'polocalcompare.muted';

/** Ceiling on the master bus. Cues are mixed well below unity so two overlapping never clip. */
const MASTER_GAIN = 0.28;

// ── Context ──────────────────────────────────────────────────────────────────

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
    if (master && ctx) {
        master.gain.setTargetAtTime(muted ? 0 : MASTER_GAIN, ctx.currentTime, 0.01);
    }
    return muted;
}

// ── Primitives ───────────────────────────────────────────────────────────────

/**
 * One shaped note. Uses setTargetAtTime for the tail rather than a linear ramp so the decay
 * sounds exponential — a linear fade reads as a synthetic "cut" rather than a note ending.
 */
function tone({ freq, type = 'sine', at = 0, dur = 0.3, gain = 0.5, glideTo = null, detune = 0 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const t0 = audio.currentTime + at;
    const osc = audio.createOscillator();
    const amp = audio.createGain();

    osc.type = type;
    osc.detune.value = detune;
    osc.frequency.setValueAtTime(freq, t0);
    if (glideTo !== null) {
        osc.frequency.exponentialRampToValueAtTime(Math.max(1, glideTo), t0 + dur);
    }

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

/** A short buffer of white noise, reused as the source for every percussive cue. */
function noiseSource(audio, seconds) {
    const frames = Math.max(1, Math.floor(audio.sampleRate * seconds));
    const buffer = audio.createBuffer(1, frames, audio.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < frames; i++) data[i] = Math.random() * 2 - 1;

    const source = audio.createBufferSource();
    source.buffer = buffer;
    return source;
}

/** Band-passed noise burst — the building block of the snare. */
function noiseHit({ at = 0, dur = 0.12, gain = 0.5, freq = 1800, q = 0.7 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const t0 = audio.currentTime + at;
    const source = noiseSource(audio, dur + 0.05);
    const filter = audio.createBiquadFilter();
    const amp = audio.createGain();

    filter.type = 'bandpass';
    filter.frequency.value = freq;
    filter.Q.value = q;

    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(gain, t0 + 0.004);
    amp.gain.setTargetAtTime(0.0001, t0 + 0.004, dur / 3);

    source.connect(filter);
    filter.connect(amp);
    amp.connect(master);

    source.start(t0);
    source.stop(t0 + dur + 0.05);
    source.onended = () => { try { amp.disconnect(); } catch { } };
}

// ── Cues ─────────────────────────────────────────────────────────────────────

/**
 * The pre-duel snare roll: accelerating noise hits that tighten and get louder, then an accent.
 * Spacing shrinks geometrically, which is what makes it read as a roll building to something
 * rather than a metronome.
 */
export function playSnareRoll() {
    if (!ensureCtx()) return;

    let at = 0;
    let spacing = 0.075;

    for (let i = 0; i < 16; i++) {
        const progress = i / 15;
        noiseHit({
            at,
            dur: 0.05,
            gain: 0.10 + progress * 0.22,
            freq: 1500 + progress * 900,
            q: 0.8,
        });
        at += spacing;
        spacing *= 0.90;
    }

    // The accent the roll was building to.
    noiseHit({ at: at + 0.02, dur: 0.22, gain: 0.45, freq: 2400, q: 0.5 });
    tone({ freq: 160, type: 'sine', at: at + 0.02, dur: 0.28, gain: 0.35, glideTo: 60 });
}

/** Verdict recorded — a bright major arpeggio. */
export function playSuccess() {
    // C6 E6 G6 C7: a plain major triad resolving up an octave.
    const notes = [1046.5, 1318.5, 1568.0, 2093.0];
    notes.forEach((freq, i) => {
        tone({ freq, type: 'triangle', at: i * 0.065, dur: 0.34, gain: 0.30 });
    });
    // A quiet fifth underneath gives it body without muddying the melody.
    tone({ freq: 523.25, type: 'sine', at: 0, dur: 0.5, gain: 0.16 });
}

/** Tournament champion — a longer, wider fanfare so the final reads bigger than a duel. */
export function playFanfare() {
    const notes = [523.25, 659.25, 783.99, 1046.5, 1318.5];
    notes.forEach((freq, i) => {
        // Two detuned saws per note: the beating between them is what makes it sound brassy
        // rather than like a test tone.
        tone({ freq, type: 'sawtooth', at: i * 0.11, dur: 0.5, gain: 0.13, detune: -7 });
        tone({ freq, type: 'sawtooth', at: i * 0.11, dur: 0.5, gain: 0.13, detune: +7 });
    });
    tone({ freq: 261.63, type: 'sine', at: 0.44, dur: 1.1, gain: 0.22 });
    noiseHit({ at: 0.44, dur: 0.6, gain: 0.18, freq: 3200, q: 0.4 });
}

/** A judged draw — deliberately unresolved, neither up nor down. */
export function playTie() {
    tone({ freq: 587.33, type: 'triangle', at: 0, dur: 0.4, gain: 0.24 });
    tone({ freq: 587.33, type: 'triangle', at: 0.16, dur: 0.45, gain: 0.20 });
}

/** A model failed or a run was abandoned — a short descending minor third. */
export function playDefeat() {
    tone({ freq: 392.0, type: 'triangle', at: 0, dur: 0.34, gain: 0.24, glideTo: 329.63 });
    tone({ freq: 196.0, type: 'sine', at: 0.05, dur: 0.5, gain: 0.18 });
}

/** UI tick for selection. Very short and quiet — it fires often. */
export function playTick() {
    tone({ freq: 1320, type: 'square', at: 0, dur: 0.035, gain: 0.07 });
}

/** Panel/navigation whoosh: noise swept downward by a moving low-pass. */
export function playWhoosh() {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const t0 = audio.currentTime;
    const source = noiseSource(audio, 0.4);
    const filter = audio.createBiquadFilter();
    const amp = audio.createGain();

    filter.type = 'lowpass';
    filter.frequency.setValueAtTime(6000, t0);
    filter.frequency.exponentialRampToValueAtTime(400, t0 + 0.32);

    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(0.16, t0 + 0.05);
    amp.gain.setTargetAtTime(0.0001, t0 + 0.06, 0.09);

    source.connect(filter);
    filter.connect(amp);
    amp.connect(master);

    source.start(t0);
    source.stop(t0 + 0.45);
    source.onended = () => { try { amp.disconnect(); } catch { } };
}

// ── Live race blips ──────────────────────────────────────────────────────────

let lastBlipAt = 0;

/**
 * A blip whose pitch tracks generation speed, so the race is audible as well as visible.
 *
 * Throttled hard: token batches arrive many times per second per side, and one oscillator per
 * batch would be both a wall of noise and a genuine allocation problem over a long duel. One
 * blip per 130ms per side is enough to hear the pace change.
 *
 * @param {number} velocity tokens/second
 * @param {string} side 'Left' or 'Right' — panned so the two are distinguishable.
 */
export function playTokenBlip(velocity, side) {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    const now = audio.currentTime;
    if (now - lastBlipAt < 0.13) return;
    lastBlipAt = now;

    // Map a plausible 0–120 tok/s onto just over an octave. Clamped so an outlier reading
    // cannot produce an inaudible or piercing note.
    const normalized = Math.max(0, Math.min(1, (velocity || 0) / 120));
    const freq = 330 + normalized * 440;

    const osc = audio.createOscillator();
    const amp = audio.createGain();
    const pan = audio.createStereoPanner ? audio.createStereoPanner() : null;

    osc.type = 'sine';
    osc.frequency.value = freq;

    amp.gain.setValueAtTime(0.0001, now);
    amp.gain.exponentialRampToValueAtTime(0.05, now + 0.006);
    amp.gain.setTargetAtTime(0.0001, now + 0.006, 0.02);

    osc.connect(amp);
    if (pan) {
        pan.pan.value = side === 'Left' ? -0.5 : 0.5;
        amp.connect(pan);
        pan.connect(master);
    } else {
        amp.connect(master);
    }

    osc.start(now);
    osc.stop(now + 0.1);
    osc.onended = () => { try { amp.disconnect(); } catch { } };
}
