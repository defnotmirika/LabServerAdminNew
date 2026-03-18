"""
enrollor.py
-----------
Handles speaker enrollment. Designed to be called from WPF Settings.

WPF usage:
    Process.Start("python", "enrollor.py --name \"Alice\"");
    Process.Start("python", "enrollor.py --name \"Alice\" --overwrite");

Output lines (JSON to stdout — WPF reads them):
    {"status": "ready",   "message": "Starting enrollment for Alice"}
    {"status": "sample",  "current": 1, "total": 3}
    {"status": "done",    "speaker": "Alice"}
    {"status": "error",   "reason": "already_exists"}
    {"status": "error",   "reason": "no_valid_samples"}
    {"status": "error",   "reason": "no_audio_backend"}

Arguments:
    --name      Speaker name (required)
    --samples   Number of voice samples to record (default: 3)
    --duration  Duration of each sample in seconds (default: 5)
    --overwrite Overwrite existing profile if found (flag, no value needed)
"""

import argparse
import json
import sys
import numpy as np

from voice_auth.audio_recorder import AudioRecorder
from voice_auth.feature_extractor import extract_mfcc
from voice_auth.speaker_model import SpeakerModel
from voice_auth.profile_manager import save_profile, profile_exists


DEFAULT_N_SAMPLES = 3
DEFAULT_DURATION = 5.0


def _emit(data: dict):
    """Write a JSON line to stdout so WPF can read it."""
    print(json.dumps(data), flush=True)


def enroll_speaker(
    name: str,
    n_samples: int = DEFAULT_N_SAMPLES,
    duration: float = DEFAULT_DURATION,
    overwrite: bool = False,
) -> bool:
    if profile_exists(name) and not overwrite:
        _emit({"status": "error", "reason": "already_exists", "speaker": name})
        return False

    _emit({"status": "ready", "message": f"Starting enrollment for {name}", "total_samples": n_samples})

    recorder = AudioRecorder()
    all_features = []

    for i in range(n_samples):
        _emit({"status": "sample", "current": i + 1, "total": n_samples})
        audio = recorder.record(duration)
        features = extract_mfcc(audio)
        if features is None or len(features) == 0:
            _emit({"status": "warning", "message": f"Sample {i + 1} produced no features, skipping"})
            continue
        all_features.append(features)

    if not all_features:
        _emit({"status": "error", "reason": "no_valid_samples"})
        return False

    combined = np.vstack(all_features)
    model = SpeakerModel()
    model.train(combined)
    save_profile(name=name, model=model, n_samples=len(all_features))

    _emit({"status": "done", "speaker": name})
    return True


def main():
    parser = argparse.ArgumentParser(description="Enroll a speaker voice profile")
    parser.add_argument("--name",      required=True,              help="Speaker name")
    parser.add_argument("--samples",   type=int,   default=DEFAULT_N_SAMPLES, help="Number of samples")
    parser.add_argument("--duration",  type=float, default=DEFAULT_DURATION,  help="Sample duration in seconds")
    parser.add_argument("--overwrite", action="store_true",        help="Overwrite existing profile")
    args = parser.parse_args()

    try:
        enroll_speaker(
            name=args.name,
            n_samples=args.samples,
            duration=args.duration,
            overwrite=args.overwrite,
        )
    except Exception as e:
        _emit({"status": "error", "reason": str(e)})
        sys.exit(1)


if __name__ == "__main__":
    main()