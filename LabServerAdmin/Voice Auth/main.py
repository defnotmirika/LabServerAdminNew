"""
main.py
-------
WPF entry point for the Voice Authentication system.

All output is JSON written to stdout — WPF reads it line by line.

Usage:
    python main.py --mode listener
    python main.py --mode enroll --name "Alice"
    python main.py --mode enroll --name "Alice" --samples 3 --duration 5 --overwrite
    python main.py --mode train  --name "Alice" --folder "C:/Training Voice/Alice"
    python main.py --mode authenticate --name "Alice"
    python main.py --mode identify
    python main.py --mode list
    python main.py --mode delete --name "Alice"

Output lines (JSON):
    {"status": "ready",    ...}
    {"status": "done",     ...}
    {"status": "error",    "reason": "..."}
    {"accepted": true/false, "speaker": "..."}
"""

import json
import sys
import argparse
import wave
from pathlib import Path

import numpy as np

from voice_auth.audio_recorder import AudioRecorder
from voice_auth.feature_extractor import extract_mfcc
from voice_auth.speaker_model import SpeakerModel
from voice_auth.profile_manager import (
    save_profile,
    load_all_profiles,
    profile_exists,
    _profile_path,
)
from voice_auth.authenticator import authenticate, identify
from voice_auth.enrollor import enroll_speaker


def emit(data: dict):
    print(json.dumps(data), flush=True)


# ---------------------------------------------------------------------------
# Modes
# ---------------------------------------------------------------------------

def mode_listener():
    from voice_auth.voice_listener import main as listener_main
    listener_main()


def mode_enroll(name: str, samples: int, duration: float, overwrite: bool):
    enroll_speaker(name=name, n_samples=samples, duration=duration, overwrite=overwrite)


def mode_train(name: str, folder: str, overwrite: bool):
    folder_path = Path(folder)
    if not folder_path.exists() or not folder_path.is_dir():
        emit({"status": "error", "reason": "folder_not_found", "folder": folder})
        sys.exit(1)

    wav_files = sorted(folder_path.glob("*.wav"))
    if not wav_files:
        emit({"status": "error", "reason": "no_wav_files", "folder": folder})
        sys.exit(1)

    if profile_exists(name) and not overwrite:
        emit({"status": "error", "reason": "already_exists", "speaker": name})
        sys.exit(1)

    emit({"status": "ready", "message": f"Training from {len(wav_files)} file(s)", "speaker": name})

    all_features = []
    for wav_path in wav_files:
        try:
            with wave.open(str(wav_path), "rb") as wf:
                n_frames = wf.getnframes()
                raw = wf.readframes(n_frames)
                sample_width = wf.getsampwidth()
                n_channels = wf.getnchannels()
                framerate = wf.getframerate()

            if sample_width == 2:
                audio = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
            elif sample_width == 4:
                audio = np.frombuffer(raw, dtype=np.int32).astype(np.float32) / 2147483648.0
            else:
                emit({"status": "warning", "message": f"Skipped {wav_path.name}: unsupported sample width"})
                continue

            if n_channels > 1:
                audio = audio.reshape(-1, n_channels).mean(axis=1)

            features = extract_mfcc(audio, sample_rate=framerate)
            if features is None or len(features) == 0:
                emit({"status": "warning", "message": f"Skipped {wav_path.name}: no features"})
                continue

            all_features.append(features)
        except Exception as e:
            emit({"status": "warning", "message": f"Skipped {wav_path.name}: {e}"})

    if not all_features:
        emit({"status": "error", "reason": "no_valid_samples"})
        sys.exit(1)

    combined = np.vstack(all_features)
    model = SpeakerModel()
    model.train(combined)
    save_profile(name=name, model=model, n_samples=len(all_features))

    emit({"status": "done", "speaker": name, "files_used": len(all_features)})


def mode_authenticate(name: str, duration: float):
    if not profile_exists(name):
        emit({"status": "error", "reason": "speaker_not_found", "speaker": name})
        sys.exit(1)

    recorder = AudioRecorder()
    emit({"status": "recording"})
    audio = recorder.record(duration)

    features = extract_mfcc(audio)
    if features is None or len(features) == 0:
        emit({"status": "error", "reason": "no_features"})
        sys.exit(1)

    result = authenticate(name, features)
    emit({
        "accepted": result.accepted,
        "speaker": result.matched_name,
        "score": round(result.score, 4),
        "threshold": round(result.threshold, 4),
    })


def mode_identify(duration: float):
    all_profiles = load_all_profiles()
    if not all_profiles:
        emit({"status": "error", "reason": "no_speakers_enrolled"})
        sys.exit(1)

    recorder = AudioRecorder()
    emit({"status": "recording"})
    audio = recorder.record(duration)

    features = extract_mfcc(audio)
    if features is None or len(features) == 0:
        emit({"status": "error", "reason": "no_features"})
        sys.exit(1)

    result = identify(features)
    emit({
        "accepted": result.accepted,
        "speaker": result.matched_name,
        "score": round(result.score, 4),
        "all_scores": {k: round(v, 4) for k, v in result.all_scores.items()},
    })


def mode_list():
    all_profiles = load_all_profiles()
    speakers = []
    for name, profile in all_profiles.items():
        entry: dict = {"name": name}
        if isinstance(profile, dict):
            entry["enrolled_at"] = profile.get("enrolled_at")
            entry["n_samples"] = profile.get("n_samples")
        speakers.append(entry)
    emit({"status": "done", "speakers": speakers})


def mode_delete(name: str):
    if not profile_exists(name):
        emit({"status": "error", "reason": "speaker_not_found", "speaker": name})
        sys.exit(1)

    try:
        _profile_path(name).unlink()
        emit({"status": "done", "speaker": name})
    except Exception as e:
        emit({"status": "error", "reason": str(e)})
        sys.exit(1)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="Voice Authentication – WPF entry point")
    parser.add_argument("--mode", required=True,
                        choices=["listener", "enroll", "train", "authenticate", "identify", "list", "delete"],
                        help="Operating mode")
    parser.add_argument("--name",      help="Speaker name")
    parser.add_argument("--folder",    help="Folder of WAV files (train mode)")
    parser.add_argument("--samples",   type=int,   default=3,   help="Number of mic samples (enroll)")
    parser.add_argument("--duration",  type=float, default=5.0, help="Recording duration in seconds")
    parser.add_argument("--overwrite", action="store_true",     help="Overwrite existing profile")
    args = parser.parse_args()

    if args.mode == "listener":
        mode_listener()

    elif args.mode == "enroll":
        if not args.name:
            emit({"status": "error", "reason": "missing_name"})
            sys.exit(1)
        mode_enroll(args.name, args.samples, args.duration, args.overwrite)

    elif args.mode == "train":
        if not args.name or not args.folder:
            emit({"status": "error", "reason": "missing_name_or_folder"})
            sys.exit(1)
        mode_train(args.name, args.folder, args.overwrite)

    elif args.mode == "authenticate":
        if not args.name:
            emit({"status": "error", "reason": "missing_name"})
            sys.exit(1)
        mode_authenticate(args.name, args.duration)

    elif args.mode == "identify":
        mode_identify(args.duration)

    elif args.mode == "list":
        mode_list()

    elif args.mode == "delete":
        if not args.name:
            emit({"status": "error", "reason": "missing_name"})
            sys.exit(1)
        mode_delete(args.name)


if __name__ == "__main__":
    main()
