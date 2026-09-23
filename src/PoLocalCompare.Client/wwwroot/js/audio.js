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
 * the tok/s figure the way a GPU render loop would.
 */

let ctx = null;
let master = null;
let muted = false;

// ── Bus layout ───────────────────────────────────────────────────────────────
//
//   cues ──► sfxBus ──┬──────────────────────► master (mute) ──► limiter ──► speakers
//                     └─► reverbSend ─► room ─┘
//   blips/drone ──► blipBus ─┘ (ducked under the payoff cues)
//
// One compressor on the way out so overlapping cues (a gavel over the coin cascade over the
// last token blips) glue together instead of clipping, and one synthetic room so every cue
// sounds like it happened in the same place. Both are built from code: the room's impulse
// response is generated noise, for the same no-assets reason as everything else here.
let sfxBus = null;
let blipBus = null;

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

        const limiter = ctx.createDynamicsCompressor();
        limiter.threshold.value = -18;
        limiter.knee.value = 12;
        limiter.ratio.value = 4;
        limiter.attack.value = 0.004;
        limiter.release.value = 0.2;
        master.connect(limiter);
        limiter.connect(ctx.destination);

        sfxBus = ctx.createGain();
        blipBus = ctx.createGain();
        sfxBus.connect(master);
        blipBus.connect(master);

        const room = ctx.createConvolver();
        room.buffer = roomImpulse(ctx, 1.6);
        const reverbSend = ctx.createGain();
        reverbSend.gain.value = 0.22;
        sfxBus.connect(reverbSend);
        reverbSend.connect(room);
        room.connect(master);
    } catch {
        ctx = null;
        master = null;
        sfxBus = null;
        blipBus = null;
    }

    return ctx;
}

/**
 * A stereo impulse response made of noise under an exponential decay — a small, bright room.
 * Independent noise per channel is what gives it width; identical channels would sound mono.
 */
function roomImpulse(audio, seconds) {
    const frames = Math.floor(audio.sampleRate * seconds);
    const buffer = audio.createBuffer(2, frames, audio.sampleRate);
    for (let ch = 0; ch < 2; ch++) {
        const data = buffer.getChannelData(ch);
        for (let i = 0; i < frames; i++) {
            data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / frames, 3.2);
        }
    }
    return buffer;
}

/**
 * Pulls the blip bus (token blips and the ambient drone) down under a payoff cue, then lets it
 * back up. Without it the verdict gavel lands on top of the last few racing blips and reads
 * as clutter rather than as the moment.
 */
function duck(depth = 0.18, seconds = 1.1) {
    if (!ctx || !blipBus) return;
    const t = ctx.currentTime;
    blipBus.gain.cancelScheduledValues(t);
    blipBus.gain.setTargetAtTime(depth, t, 0.03);
    blipBus.gain.setTargetAtTime(1, t + seconds, 0.25);
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
    // A drone left running under a zero master gain still costs an audio graph for nothing.
    if (muted) setAmbientDrone(false);
    return muted;
}

// ── Primitives ───────────────────────────────────────────────────────────────

/**
 * One shaped note. Uses setTargetAtTime for the tail rather than a linear ramp so the decay
 * sounds exponential — a linear fade reads as a synthetic "cut" rather than a note ending.
 */
function tone({ freq, type = 'sine', at = 0, dur = 0.3, gain = 0.5, glideTo = null, detune = 0, bus = null }) {
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
    amp.connect(bus ?? sfxBus);

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
    amp.connect(sfxBus);

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
    duck(0.25, 0.8);
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
    duck(0.12, 2.0);
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
    amp.connect(sfxBus);

    source.start(t0);
    source.stop(t0 + 0.45);
    source.onended = () => { try { amp.disconnect(); } catch { } };
}

// ── The duet: each model gets a voice ────────────────────────────────────────

/**
 * Musical identity derived from a model id, so the same model always sounds the same across
 * duels — a sonic signature rather than a random patch. The hash picks a root, a scale and a
 * timbre; it is FNV-1a because it is five lines and distributes short ids well.
 */
const SCALES = [
    [0, 2, 4, 7, 9],        // major pentatonic
    [0, 3, 5, 7, 10],       // minor pentatonic
    [0, 2, 3, 5, 7, 9, 10], // dorian
    [0, 2, 4, 6, 7, 9, 11], // lydian
];
const TIMBRES = [
    { type: 'sine', fm: 2 },
    { type: 'triangle', fm: 3 },
    { type: 'sine', fm: 1.5 },
    { type: 'triangle', fm: 1 },
];

function hashSeed(seed) {
    let h = 0x811c9dc5;
    const text = String(seed ?? '');
    for (let i = 0; i < text.length; i++) {
        h ^= text.charCodeAt(i);
        h = Math.imul(h, 0x01000193);
    }
    return h >>> 0;
}

function voiceFor(seed) {
    const h = hashSeed(seed);
    return {
        root: 57 + (h % 8),                 // A3 … E4 (MIDI)
        scale: SCALES[(h >>> 3) % SCALES.length],
        timbre: TIMBRES[(h >>> 6) % TIMBRES.length],
        stride: 1 + ((h >>> 9) % 2),        // how far the melody walks per note
    };
}

const duet = {
    Left: { voice: voiceFor('left'), step: 0, dir: 1, lastAt: 0, panner: null },
    Right: { voice: voiceFor('right'), step: 0, dir: 1, lastAt: 0, panner: null },
};

/**
 * Assigns the two voices for a duel. When two models hash to the same root and scale, the
 * right-hand one moves up a fourth — a duet in unison is one voice, and the point is to hear
 * two.
 */
export function setDuetVoices(leftSeed, rightSeed) {
    const left = voiceFor(leftSeed);
    const right = voiceFor(rightSeed);
    if (left.root === right.root && left.scale === right.scale) right.root += 5;

    duet.Left = { voice: left, step: 0, dir: 1, lastAt: 0, panner: duet.Left.panner };
    duet.Right = { voice: right, step: 0, dir: 1, lastAt: 0, panner: duet.Right.panner };
}

/**
 * A spatial panner per side, reused for the whole duel. HRTF places the left model to your
 * left and slightly ahead rather than just louder in one ear, which is what makes two
 * overlapping melodies separable on headphones. Falls back to a stereo panner, then to none.
 */
function pannerFor(audio, side) {
    const lane = duet[side];
    if (lane.panner) return lane.panner;

    const x = side === 'Left' ? -1.6 : 1.6;
    try {
        const p = audio.createPanner();
        p.panningModel = 'HRTF';
        p.distanceModel = 'inverse';
        p.refDistance = 1;
        if (p.positionX) {
            p.positionX.value = x;
            p.positionY.value = 0;
            p.positionZ.value = -1;
        } else {
            p.setPosition(x, 0, -1);
        }
        p.connect(blipBus);
        lane.panner = p;
    } catch {
        try {
            const p = audio.createStereoPanner();
            p.pan.value = side === 'Left' ? -0.6 : 0.6;
            p.connect(blipBus);
            lane.panner = p;
        } catch {
            lane.panner = blipBus;
        }
    }
    return lane.panner;
}

/**
 * One note of a model's melody. Pace is audible two ways: the faster model plays MORE notes
 * (the per-side gap shrinks from ~420 ms toward 90 ms as tok/s climbs), and it plays them an
 * octave up past ~60 tok/s. FM brightness also rises with speed.
 *
 * Throttled per side — the previous version kept one timestamp for both, so whichever side
 * reported first silenced the other for the whole window.
 *
 * @param {number} velocity tokens/second
 * @param {string} side 'Left' or 'Right'
 */
export function playTokenBlip(velocity, side) {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    const lane = duet[side === 'Left' ? 'Left' : 'Right'];
    const normalized = Math.max(0, Math.min(1, (velocity || 0) / 120));
    const gap = 0.42 - normalized * 0.33;

    const now = audio.currentTime;
    if (now - lane.lastAt < gap) return;
    lane.lastAt = now;

    // Walk the scale, turning around at the ends, so it reads as a line rather than a random
    // sequence of pitches.
    const { root, scale, timbre, stride } = lane.voice;
    lane.step += lane.dir * stride;
    if (lane.step >= scale.length * 2 || lane.step <= 0) lane.dir = -lane.dir;
    lane.step = Math.max(0, Math.min(scale.length * 2 - 1, lane.step));

    const octave = Math.floor(lane.step / scale.length) + (normalized > 0.5 ? 1 : 0);
    const midi = root + scale[lane.step % scale.length] + octave * 12;
    const freq = 440 * Math.pow(2, (midi - 69) / 12);

    const carrier = audio.createOscillator();
    const amp = audio.createGain();
    carrier.type = timbre.type;
    carrier.frequency.value = freq;

    amp.gain.setValueAtTime(0.0001, now);
    amp.gain.exponentialRampToValueAtTime(0.07, now + 0.006);
    amp.gain.setTargetAtTime(0.0001, now + 0.008, 0.045);

    const modulator = audio.createOscillator();
    const modAmp = audio.createGain();
    modulator.frequency.value = freq * timbre.fm;
    modAmp.gain.value = 20 + normalized * 220;
    modulator.connect(modAmp);
    modAmp.connect(carrier.frequency);

    carrier.connect(amp);
    amp.connect(pannerFor(audio, side === 'Left' ? 'Left' : 'Right'));

    modulator.start(now);
    carrier.start(now);
    modulator.stop(now + 0.22);
    carrier.stop(now + 0.22);
    carrier.onended = () => {
        try {
            amp.disconnect();
            modAmp.disconnect();
        } catch { }
    };
}

// ── New Procedural Audio Engines ─────────────────────────────────────────────

/** Quantum ignition sub-bass drop: 85 Hz swept down to 22 Hz. */
export function playSubDrop() {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    const t0 = audio.currentTime;
    const osc = audio.createOscillator();
    const amp = audio.createGain();
    const filter = audio.createBiquadFilter();

    osc.type = 'sine';
    osc.frequency.setValueAtTime(85, t0);
    osc.frequency.exponentialRampToValueAtTime(22, t0 + 0.45);

    filter.type = 'lowpass';
    filter.frequency.value = 130;

    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(0.35, t0 + 0.02);
    amp.gain.setTargetAtTime(0.0001, t0 + 0.05, 0.15);

    osc.connect(filter);
    filter.connect(amp);
    amp.connect(sfxBus);

    osc.start(t0);
    osc.stop(t0 + 0.5);
    osc.onended = () => { try { amp.disconnect(); } catch { } };
}

/** Pre-duel ignition clash: dual panned rising sweeps meeting with a metallic accent. */
export function playIgnitionClash() {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    tone({ freq: 130, glideTo: 390, type: 'sawtooth', at: 0, dur: 0.32, gain: 0.12, detune: -5 });
    tone({ freq: 165, glideTo: 495, type: 'sawtooth', at: 0, dur: 0.32, gain: 0.12, detune: 5 });

    setTimeout(() => {
        noiseHit({ at: 0, dur: 0.18, gain: 0.32, freq: 2800, q: 1.2 });
        tone({ freq: 880, glideTo: 440, type: 'triangle', at: 0, dur: 0.25, gain: 0.22 });
        tone({ freq: 110, glideTo: 45, type: 'sine', at: 0, dur: 0.3, gain: 0.35 });
    }, 320);
}

/** Photo-finish sonic boom: supersonic noise crack + low-frequency resonance. */
export function playShockwave() {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    duck(0.3, 0.9);
    noiseHit({ at: 0, dur: 0.06, gain: 0.4, freq: 4800, q: 0.5 });
    tone({ freq: 100, glideTo: 32, type: 'sine', at: 0.01, dur: 0.65, gain: 0.38 });
}

/** The AI Judge verdict impact: sub-punch + filtered noise slap + major chord resolve. */
export function playGavelImpact() {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    duck(0.1, 1.6);
    tone({ freq: 120, glideTo: 38, type: 'sine', at: 0, dur: 0.28, gain: 0.45 });
    noiseHit({ at: 0, dur: 0.08, gain: 0.35, freq: 1400, q: 1.0 });

    const chord = [523.25, 659.25, 783.99, 1046.5];
    chord.forEach((freq, i) => {
        tone({ freq, type: 'triangle', at: 0.08 + i * 0.035, dur: 0.45, gain: 0.18 });
    });
}

/** Elo rating transfer: ascending pentatonic coin cascade with micro-detuning. */
export function playCoinCascade() {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    const notes = [1046.5, 1174.66, 1318.51, 1567.98, 1760.0, 2093.0, 2349.32, 2637.02];
    notes.forEach((freq, i) => {
        const microDetune = (Math.random() - 0.5) * 14;
        tone({
            freq,
            type: 'sine',
            at: i * 0.032,
            dur: 0.18,
            gain: 0.14,
            detune: microDetune
        });
    });
}

/** Green score resonance chime: 528 Hz Solfeggio pure sine + fifth. */
export function playHarmonicChime() {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    tone({ freq: 528.0, type: 'sine', at: 0, dur: 1.4, gain: 0.22 });
    tone({ freq: 792.0, type: 'sine', at: 0.04, dur: 1.2, gain: 0.14 });
    tone({ freq: 1056.0, type: 'triangle', at: 0.08, dur: 0.9, gain: 0.08 });
}

/** Dual-action tactile keyboard switch clicks (downstroke thud / upstroke snap). */
export function playMechanicalClick(isDown = true) {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    if (isDown) {
        tone({ freq: 340, glideTo: 180, type: 'triangle', at: 0, dur: 0.025, gain: 0.12 });
        noiseHit({ at: 0, dur: 0.015, gain: 0.08, freq: 1200, q: 1.5 });
    } else {
        noiseHit({ at: 0, dur: 0.012, gain: 0.14, freq: 2600, q: 1.8 });
        tone({ freq: 1800, type: 'sine', at: 0, dur: 0.018, gain: 0.06 });
    }
}

// ── Generative Ambient Cyber-Drone ───────────────────────────────────────────

let ambientNodes = null;

/** Ambient generative low-frequency cyber-drone (-32 dB) with slow binaural beat. */
export function setAmbientDrone(enabled) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    if (!enabled || muted) {
        if (ambientNodes) {
            try {
                ambientNodes.gain.gain.setTargetAtTime(0.0001, audio.currentTime, 0.4);
                const toClean = ambientNodes;
                ambientNodes = null;
                setTimeout(() => {
                    try {
                        toClean.osc1.stop();
                        toClean.osc2.stop();
                        toClean.gain.disconnect();
                    } catch { }
                }, 500);
            } catch { }
        }
        return;
    }

    if (ambientNodes) return;

    try {
        const now = audio.currentTime;
        const osc1 = audio.createOscillator();
        const osc2 = audio.createOscillator();
        const filter = audio.createBiquadFilter();
        const gain = audio.createGain();

        osc1.type = 'sine';
        osc1.frequency.value = 55.0; // A1
        osc2.type = 'sine';
        osc2.frequency.value = 55.4; // 0.4 Hz binaural beat

        filter.type = 'lowpass';
        filter.frequency.value = 160;

        gain.gain.setValueAtTime(0.0001, now);
        gain.gain.exponentialRampToValueAtTime(0.025, now + 1.2);

        osc1.connect(filter);
        osc2.connect(filter);
        filter.connect(gain);
        gain.connect(blipBus);

        osc1.start(now);
        osc2.start(now);

        ambientNodes = { osc1, osc2, filter, gain };
    } catch { }
}


// ── Judge, board and bracket cues ────────────────────────────────────────────

/**
 * One heartbeat of the judge countdown: a lub-dub of two low thumps. Urgency (0…1) raises the
 * level and tightens the gap between the two beats, so the last seconds feel closer together
 * even though the Arena still calls this once per second.
 */
export function playHeartbeat(urgency = 0) {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    const u = Math.max(0, Math.min(1, urgency));
    const gain = 0.22 + u * 0.28;
    const gap = 0.24 - u * 0.1;

    tone({ freq: 62, glideTo: 38, type: 'sine', at: 0, dur: 0.16, gain });
    tone({ freq: 55, glideTo: 34, type: 'sine', at: gap, dur: 0.2, gain: gain * 0.75 });
}

/** A soft rising scan under the judge's reticle as it starts looking. */
export function playScanSweep() {
    const audio = ensureCtx();
    if (!audio || !master || muted) return;

    const t0 = audio.currentTime;
    const source = noiseSource(audio, 1.3);
    const filter = audio.createBiquadFilter();
    const amp = audio.createGain();

    filter.type = 'bandpass';
    filter.Q.value = 6;
    filter.frequency.setValueAtTime(500, t0);
    filter.frequency.exponentialRampToValueAtTime(4200, t0 + 1.1);

    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(0.09, t0 + 0.2);
    amp.gain.setTargetAtTime(0.0001, t0 + 0.9, 0.12);

    source.connect(filter);
    filter.connect(amp);
    amp.connect(sfxBus);
    source.start(t0);
    source.stop(t0 + 1.3);
    source.onended = () => { try { amp.disconnect(); } catch { } };
}

/** A model moved on the leaderboard since you last looked: up is a rising fifth, down a falling third. */
export function playRankShift(direction) {
    if (direction > 0) {
        tone({ freq: 880, type: 'triangle', at: 0, dur: 0.18, gain: 0.14 });
        tone({ freq: 1318.5, type: 'triangle', at: 0.07, dur: 0.28, gain: 0.14 });
    } else {
        tone({ freq: 659.25, type: 'sine', at: 0, dur: 0.18, gain: 0.1 });
        tone({ freq: 523.25, type: 'sine', at: 0.08, dur: 0.26, gain: 0.1 });
    }
}

/** A bracket winner's light travelling to the next round. */
export function playAdvance() {
    tone({ freq: 523.25, glideTo: 1046.5, type: 'triangle', at: 0, dur: 0.42, gain: 0.12 });
    tone({ freq: 1568.0, type: 'sine', at: 0.36, dur: 0.4, gain: 0.12 });
}

/** A bracket loser dropping out: a low thud and a downward glide. */
export function playKnockout() {
    tone({ freq: 140, glideTo: 50, type: 'sine', at: 0, dur: 0.3, gain: 0.3 });
    tone({ freq: 392.0, glideTo: 196.0, type: 'triangle', at: 0.02, dur: 0.36, gain: 0.08 });
}
