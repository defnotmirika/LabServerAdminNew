"""
voice_listener.py
-----------------
Always-on voice listener for WPF integration.

- Opens the microphone and listens continuously
- Uses energy-based Voice Activity Detection (VAD)
- When speech is detected, captures the utterance
- Authenticates the speaker
- If accepted, emits JSON to stdout — WPF reads it and handles commands
- Loops back to listening
- Stops cleanly when the process is killed (Voice OFF)

WPF usage:
    // Voice ON
    _process = Process.Start("python", "voice_listener.py");
    _process.OutputDataReceived += (s, e) => HandleCommand(e.Data);
    _process.BeginOutputReadLine();

    // Voice OFF
    _process.Kill();

Output lines:
    {"status": "listening"}
    {"accepted": true,  "speaker": "Alice"}
    {"accepted": false, "speaker": null, "reason": "auth_failed"}
    {"status": "error", "reason": "no_speakers_enrolled"}
    {"status": "stopped"}
"""

import json
import sys
import signal
from pathlib import Path

import numpy as np

try:
    import sounddevice as sd
    SOUNDDEVICE_AVAILABLE = True
except ImportError:
    SOUNDDEVICE_AVAILABLE = False

try:
    import pyaudio
    PYAUDIO_AVAILABLE = True
except ImportError:
    PYAUDIO_AVAILABLE = False


SAMPLE_RATE = 16000
CHUNK_DURATION = 0.03
CHUNK_SIZE = int(SAMPLE_RATE * CHUNK_DURATION)

ENERGY_THRESHOLD = 0.01
SILENCE_CHUNKS_TO_STOP = 20
MIN_SPEECH_CHUNKS = 10
MAX_SPEECH_SECONDS = 6
from voice_auth import BASE_DIR
PROFILE_DIR = BASE_DIR / "speaker_profiles"

_extract_mfcc = None
_identify = None


def _emit(data: dict):
    """Write a JSON line to stdout so WPF can read it."""
    print(json.dumps(data), flush=True)


def _has_enrolled_profiles() -> bool:
    return PROFILE_DIR.exists() and any(PROFILE_DIR.glob("*.json"))


def _ensure_recognition_modules():
    global _extract_mfcc, _identify

    if _extract_mfcc is None or _identify is None:
        from voice_auth.feature_extractor import extract_mfcc
        from voice_auth.authenticator import identify

        _extract_mfcc = extract_mfcc
        _identify = identify


def _handle_utterance(audio: np.ndarray) -> dict:
    """
    Authenticate a captured utterance.
    Returns a result dict — WPF decides what command to run.
    """
    _ensure_recognition_modules()

    features = _extract_mfcc(audio)
    if features is None or len(features) == 0:
        return {"accepted": False, "speaker": None, "reason": "no_features"}

    result = _identify(features)

    if not result.accepted:
        return {"accepted": False, "speaker": None, "reason": "auth_failed"}

    return {
        "accepted": True,
        "speaker": result.matched_name,
    }


def _listen_sounddevice():
    """Continuous listening loop using sounddevice."""
    buffer = []
    recording = False
    silence_count = 0
    speech_chunks = 0
    max_chunks = int(MAX_SPEECH_SECONDS / CHUNK_DURATION)

    _emit({"status": "listening"})

    with sd.InputStream(
        samplerate=SAMPLE_RATE,
        channels=1,
        dtype="float32",
        blocksize=CHUNK_SIZE,
    ) as stream:
        while True:
            chunk, _ = stream.read(CHUNK_SIZE)
            chunk = chunk.flatten()
            energy = float(np.sqrt(np.mean(chunk**2)))

            if not recording:
                if energy > ENERGY_THRESHOLD:
                    recording = True
                    silence_count = 0
                    speech_chunks = 1
                    buffer = [chunk]
            else:
                buffer.append(chunk)
                speech_chunks += 1

                if energy < ENERGY_THRESHOLD:
                    silence_count += 1
                else:
                    silence_count = 0

                stopped_by_silence = silence_count >= SILENCE_CHUNKS_TO_STOP
                stopped_by_max = speech_chunks >= max_chunks

                if stopped_by_silence or stopped_by_max:
                    if speech_chunks >= MIN_SPEECH_CHUNKS:
                        audio = np.concatenate(buffer)
                        result = _handle_utterance(audio)
                        _emit(result)
                    recording = False
                    buffer = []
                    silence_count = 0
                    speech_chunks = 0
                    _emit({"status": "listening"})


def _listen_pyaudio():
    """Continuous listening loop using pyaudio."""
    import pyaudio

    pa = pyaudio.PyAudio()
    stream = pa.open(
        format=pyaudio.paFloat32,
        channels=1,
        rate=SAMPLE_RATE,
        input=True,
        frames_per_buffer=CHUNK_SIZE,
    )

    buffer = []
    recording = False
    silence_count = 0
    speech_chunks = 0
    max_chunks = int(MAX_SPEECH_SECONDS / CHUNK_DURATION)

    _emit({"status": "listening"})

    try:
        while True:
            raw = stream.read(CHUNK_SIZE, exception_on_overflow=False)
            chunk = np.frombuffer(raw, dtype=np.float32)
            energy = float(np.sqrt(np.mean(chunk**2)))

            if not recording:
                if energy > ENERGY_THRESHOLD:
                    recording = True
                    silence_count = 0
                    speech_chunks = 1
                    buffer = [chunk]
            else:
                buffer.append(chunk)
                speech_chunks += 1

                if energy < ENERGY_THRESHOLD:
                    silence_count += 1
                else:
                    silence_count = 0

                stopped_by_silence = silence_count >= SILENCE_CHUNKS_TO_STOP
                stopped_by_max = speech_chunks >= max_chunks

                if stopped_by_silence or stopped_by_max:
                    if speech_chunks >= MIN_SPEECH_CHUNKS:
                        audio = np.concatenate(buffer)
                        result = _handle_utterance(audio)
                        _emit(result)
                    recording = False
                    buffer = []
                    silence_count = 0
                    speech_chunks = 0
                    _emit({"status": "listening"})
    finally:
        stream.stop_stream()
        stream.close()
        pa.terminate()


def _graceful_exit(signum, frame):
    _emit({"status": "stopped"})
    sys.exit(0)


def main():
    signal.signal(signal.SIGTERM, _graceful_exit)
    signal.signal(signal.SIGINT, _graceful_exit)

    if not _has_enrolled_profiles():
        _emit({"status": "error", "reason": "no_speakers_enrolled"})
        sys.exit(1)

    try:
        if SOUNDDEVICE_AVAILABLE:
            _listen_sounddevice()
        elif PYAUDIO_AVAILABLE:
            _listen_pyaudio()
        else:
            _emit({"status": "error", "reason": "no_audio_backend"})
            sys.exit(1)
    except Exception as e:
        _emit({"status": "error", "reason": str(e)})
        sys.exit(1)


if __name__ == "__main__":
    main()