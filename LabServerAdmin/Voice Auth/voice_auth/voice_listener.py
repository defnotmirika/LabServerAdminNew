"""
voice_listener.py  (Vosk edition)
----------------------------------
Same JSON output contract as before — WPF reads it unchanged.
"""

import json
import sys
import signal
import argparse
from pathlib import Path

import numpy as np

try:
    from vosk import Model, KaldiRecognizer
    VOSK_AVAILABLE = True
except ImportError:
    VOSK_AVAILABLE = False

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
CHUNK_SIZE  = 4000          # ~250 ms per chunk — good for Vosk

# VAD / auth settings (same as before)
ENERGY_THRESHOLD      = 0.02
SILENCE_CHUNKS_TO_STOP = 40
MIN_SPEECH_CHUNKS      = 15
MAX_SPEECH_SECONDS     = 8

from voice_auth import BASE_DIR
PROFILE_DIR  = BASE_DIR / "speaker_profiles"
MODEL_PATH = BASE_DIR / "vosk-model"

_extract_mfcc = None
_authenticate  = None
_identify      = None
_active_speaker: str | None = None


def _emit(data: dict):
    print(json.dumps(data), flush=True)


def _has_enrolled_profiles() -> bool:
    return PROFILE_DIR.exists() and any(PROFILE_DIR.glob("*.json"))


def _ensure_recognition_modules():
    global _extract_mfcc, _authenticate, _identify
    if _extract_mfcc is None:
        from voice_auth.feature_extractor import extract_mfcc
        from voice_auth.authenticator    import authenticate, identify
        _extract_mfcc = extract_mfcc
        _authenticate  = authenticate
        _identify      = identify


def _handle_utterance(audio: np.ndarray, recognizer=None) -> dict:
    _ensure_recognition_modules()
    features = _extract_mfcc(audio)
    if features is None or len(features) == 0:
        return {"accepted": False, "speaker": None, "reason": "no_features"}

    if _active_speaker:
        result = _authenticate(_active_speaker, features)
    else:
        result = _identify(features)

    if not result.accepted:
        return {
            "accepted": False,
            "speaker": None,
            "reason": f"auth_failed (score:{round(result.score,1)} threshold:{round(result.threshold,1)})"
        }

    # Get Vosk transcription
    command = ""
    if recognizer:
        import json as _json
        result_json = recognizer.FinalResult()
        text = _json.loads(result_json).get("text", "").strip().lower()
        command = text

    return {
        "accepted":  True,
        "speaker":   result.matched_name,
        "command":   command,
        "score":     round(result.score, 1),
        "threshold": round(result.threshold, 1),
    }


def _load_vosk_model():
    """Load Vosk model if available, else return None."""
    if not VOSK_AVAILABLE:
        return None
    if not MODEL_PATH.exists():
        _emit({"status": "warning", "reason": f"vosk-model not found at {MODEL_PATH} — using energy VAD only"})
        return None
    try:
        model = Model(str(MODEL_PATH))
        return KaldiRecognizer(model, SAMPLE_RATE)
    except Exception as e:
        _emit({"status": "warning", "reason": f"vosk load error: {e}"})
        return None


def _listen_sounddevice(recognizer):
    buffer        = []
    recording     = False
    silence_count = 0
    speech_chunks = 0
    max_chunks    = int(MAX_SPEECH_SECONDS / (CHUNK_SIZE / SAMPLE_RATE))

    _emit({"status": "listening"})

    with sd.InputStream(samplerate=SAMPLE_RATE, channels=1,
                        dtype="int16", blocksize=CHUNK_SIZE) as stream:
        while True:
            raw, _ = stream.read(CHUNK_SIZE)
            chunk  = raw.flatten()

            # Feed to Vosk (optional — gives us text, but auth uses MFCC)
            if recognizer:
                recognizer.AcceptWaveform(chunk.tobytes())

            # Energy VAD (same logic as original)
            float_chunk = chunk.astype(np.float32) / 32768.0
            energy = float(np.sqrt(np.mean(float_chunk ** 2)))

            if not recording:
                if energy > ENERGY_THRESHOLD:
                    recording     = True
                    silence_count = 0
                    speech_chunks = 1
                    buffer        = [chunk]
            else:
                buffer.append(chunk)
                speech_chunks += 1
                silence_count  = silence_count + 1 if energy < ENERGY_THRESHOLD else 0

                if silence_count >= SILENCE_CHUNKS_TO_STOP or speech_chunks >= max_chunks:
                    if speech_chunks >= MIN_SPEECH_CHUNKS:
                        audio  = np.concatenate(buffer).astype(np.float32) / 32768.0
                        result = _handle_utterance(audio, recognizer)
                        if result.get("status") != "debug_score":
                            _emit(result)
                    recording     = False
                    buffer        = []
                    silence_count = 0
                    speech_chunks = 0
                    _emit({"status": "listening"})


def _listen_pyaudio(recognizer):
    import pyaudio
    pa     = pyaudio.PyAudio()
    stream = pa.open(format=pyaudio.paInt16, channels=1, rate=SAMPLE_RATE,
                     input=True, frames_per_buffer=CHUNK_SIZE)

    buffer        = []
    recording     = False
    silence_count = 0
    speech_chunks = 0
    max_chunks    = int(MAX_SPEECH_SECONDS / (CHUNK_SIZE / SAMPLE_RATE))

    _emit({"status": "listening"})

    try:
        while True:
            raw   = stream.read(CHUNK_SIZE, exception_on_overflow=False)
            chunk = np.frombuffer(raw, dtype=np.int16)

            if recognizer:
                recognizer.AcceptWaveform(raw)

            float_chunk = chunk.astype(np.float32) / 32768.0
            energy = float(np.sqrt(np.mean(float_chunk ** 2)))

            if not recording:
                if energy > ENERGY_THRESHOLD:
                    recording     = True
                    silence_count = 0
                    speech_chunks = 1
                    buffer        = [chunk]
            else:
                buffer.append(chunk)
                speech_chunks += 1
                silence_count  = silence_count + 1 if energy < ENERGY_THRESHOLD else 0

                if silence_count >= SILENCE_CHUNKS_TO_STOP or speech_chunks >= max_chunks:
                    if speech_chunks >= MIN_SPEECH_CHUNKS:
                        audio  = np.concatenate(buffer).astype(np.float32) / 32768.0
                        result = _handle_utterance(audio, recognizer)
                        if result.get("status") != "debug_score":
                            _emit(result)
                    recording     = False
                    buffer        = []
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


def main(name: str | None = None):
    global _active_speaker

    if name is None:
        parser = argparse.ArgumentParser()
        parser.add_argument("--name", default=None)
        args, _ = parser.parse_known_args()
        name = args.name

    _active_speaker = name.strip().lower() if name else None

    signal.signal(signal.SIGTERM, _graceful_exit)
    signal.signal(signal.SIGINT,  _graceful_exit)

    if not _has_enrolled_profiles():
        _emit({"status": "error", "reason": "no_speakers_enrolled"})
        sys.exit(1)

    recognizer = _load_vosk_model()   # None = Vosk not ready, still works

    try:
        if SOUNDDEVICE_AVAILABLE:
            _listen_sounddevice(recognizer)
        elif PYAUDIO_AVAILABLE:
            _listen_pyaudio(recognizer)
        else:
            _emit({"status": "error", "reason": "no_audio_backend"})
            sys.exit(1)
    except Exception as e:
        _emit({"status": "error", "reason": str(e)})
        sys.exit(1)


if __name__ == "__main__":
    main()
