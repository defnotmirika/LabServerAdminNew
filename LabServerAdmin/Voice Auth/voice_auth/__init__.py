"""
voice_auth package
------------------
Resolves the base directory for data files (e.g. speaker_profiles/).

- When running compiled via PyInstaller: next to the .exe
- When running from source: project root (parent of this package)
"""

import sys
from pathlib import Path

if getattr(sys, "frozen", False):
    # PyInstaller sets sys.frozen and sys._MEIPASS
    BASE_DIR: Path = Path(sys.executable).parent
else:
    BASE_DIR: Path = Path(__file__).parent.parent
