"""
profile_manager.py
------------------
Saves and loads speaker profiles to/from disk.

Each profile is stored as a JSON file in the `speaker_profiles/` directory:
  speaker_profiles/<speaker_name>.json

The JSON contains:
  - name: str
  - enrolled_at: ISO-8601 timestamp
  - n_samples: int
  - model: serialised SpeakerModel dict
"""

import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path

from voice_auth import BASE_DIR
from voice_auth.speaker_model import SpeakerModel


PROFILES_DIR = BASE_DIR / "speaker_profiles"


def _sanitise_name(name: str) -> str:
    """Convert a speaker name to a safe filename."""
    return re.sub(r"[^a-zA-Z0-9_\-]", "_", name.strip()).lower()


def _profile_path(name: str) -> Path:
    return PROFILES_DIR / f"{_sanitise_name(name)}.json"


def save_profile(name: str, model: SpeakerModel, n_samples: int) -> None:
    """Persist a speaker profile to disk."""
    PROFILES_DIR.mkdir(parents=True, exist_ok=True)
    profile = {
        "name": name,
        "enrolled_at": datetime.now(timezone.utc).isoformat(),
        "n_samples": n_samples,
        "model": model.to_dict(),
    }
    path = _profile_path(name)
    with open(path, "w") as f:
        json.dump(profile, f, indent=2)
    print(f"  Profile saved: {path}")


def load_profile(name: str) -> tuple[str, SpeakerModel] | None:
    """
    Load a speaker profile by name.

    Returns (display_name, SpeakerModel) or None if not found.
    """
    path = _profile_path(name)
    if not path.exists():
        return None
    with open(path) as f:
        profile = json.load(f)
    model = SpeakerModel.from_dict(profile["model"])
    return profile["name"], model


def list_profiles() -> list[dict]:
    """Return a list of dicts describing all enrolled speakers."""
    if not PROFILES_DIR.exists():
        return []
    profiles = []
    for path in sorted(PROFILES_DIR.glob("*.json")):
        with open(path) as f:
            data = json.load(f)
        profiles.append({
            "name": data.get("name", path.stem),
            "enrolled_at": data.get("enrolled_at", "unknown"),
            "n_samples": data.get("n_samples", 0),
        })
    return profiles


def delete_profile(name: str) -> bool:
    """Delete a speaker profile. Returns True if deleted, False if not found."""
    path = _profile_path(name)
    if path.exists():
        os.remove(path)
        return True
    return False


def profile_exists(name: str) -> bool:
    return _profile_path(name).exists()


def load_all_profiles() -> dict[str, SpeakerModel]:
    """Load every enrolled speaker's model into a dict keyed by display name."""
    result = {}
    if not PROFILES_DIR.exists():
        return result
    for path in PROFILES_DIR.glob("*.json"):
        with open(path) as f:
            data = json.load(f)
        try:
            model = SpeakerModel.from_dict(data["model"])
            result[data["name"]] = model
        except Exception as e:
            print(f"  Warning: could not load profile {path.name}: {e}")
    return result