/**
 * T052 — Web Audio API helpers for duel audio cues.
 * Pre-loads AudioBuffers from WAV files and exposes playSnareRoll() / playSuccess().
 * Gracefully no-ops if AudioContext is unavailable.
 */

let audioCtx = null;
let snareBuffer = null;
let successBuffer = null;

async function initAudio() {
    if (audioCtx) return;
    try {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        snareBuffer = await loadBuffer('/audio/snare-roll.wav');
        successBuffer = await loadBuffer('/audio/success.wav');
    } catch {
        audioCtx = null;
    }
}

async function loadBuffer(url) {
    if (!audioCtx) return null;
    try {
        const response = await fetch(url);
        const arrayBuffer = await response.arrayBuffer();
        return await audioCtx.decodeAudioData(arrayBuffer);
    } catch {
        return null;
    }
}

function playBuffer(buffer) {
    if (!audioCtx || !buffer) return;
    try {
        const source = audioCtx.createBufferSource();
        source.buffer = buffer;
        source.connect(audioCtx.destination);
        source.start(0);
    } catch {
        // Graceful no-op
    }
}

export async function playSnareRoll() {
    await initAudio();
    playBuffer(snareBuffer);
}

export async function playSuccess() {
    await initAudio();
    playBuffer(successBuffer);
}
