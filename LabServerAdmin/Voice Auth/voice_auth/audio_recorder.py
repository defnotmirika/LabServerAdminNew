"""
audio_recorder.py
-----------------
Records audio from the microphone and saves it as a WAV file or returns
raw numpy audio data. Provides helper utilities for listing input devices.
"""

import os
import wave
import tempfile
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
CHANNELS = 1
CHUNK_SIZE = 1024
SAMPLE_WIDTH = 2


class AudioRecorder:
    """Records audio from the default microphone."""

    def __init__(self, sample_rate: int = SAMPLE_RATE, channels: int = CHANNELS):
        self.sample_rate = sample_rate
        self.channels = channels

    def list_devices(self):
        """Print available audio input devices."""
        if SOUNDDEVICE_AVAILABLE:
            print("\nAvailable audio devices:")
            devices = sd.query_devices()
            for i, dev in enumerate(devices):
                if dev["max_input_channels"] > 0:
                    print(f"  [{i}] {dev['name']}")
        elif PYAUDIO_AVAILABLE:
            pa = pyaudio.PyAudio()
            print("\nAvailable audio devices:")
            for i in range(pa.get_device_count()):
                info = pa.get_device_info_by_index(i)
                if info["maxInputChannels"] > 0:
                    print(f"  [{i}] {info['name']}")
            pa.terminate()
        else:
            print("No audio backend available.")

    def record(self, duration: float, prompt: str = "Recording...") -> np.ndarray:
        """
        Record audio for `duration` seconds.

        Returns a 1-D float32 numpy array normalised to [-1, 1].
        """
        print(f"\n{prompt} (Recording for {duration}s — speak now)")

        if SOUNDDEVICE_AVAILABLE:
            audio = sd.rec(
                int(duration * self.sample_rate),
                samplerate=self.sample_rate,
                channels=self.channels,
                dtype="float32",
            )
            sd.wait()
            return audio.flatten()

        if PYAUDIO_AVAILABLE:
            return self._record_pyaudio(duration)

        raise RuntimeError(
            "No audio backend found. Install sounddevice or pyaudio:\n"
            "  pip install sounddevice\n"
            "  pip install pyaudio"
        )

    def _record_pyaudio(self, duration: float) -> np.ndarray:
        pa = pyaudio.PyAudio()
        stream = pa.open(
            format=pyaudio.paInt16,
            channels=self.channels,
            rate=self.sample_rate,
            input=True,
            frames_per_buffer=CHUNK_SIZE,
        )
        frames = []
        n_chunks = int(self.sample_rate / CHUNK_SIZE * duration)
        for _ in range(n_chunks):
            data = stream.read(CHUNK_SIZE, exception_on_overflow=False)
            frames.append(data)
        stream.stop_stream()
        stream.close()
        pa.terminate()

        raw = b"".join(frames)
        audio = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
        return audio

    def record_to_wav(self, duration: float, filepath: str | None = None, prompt: str = "Recording...") -> str:
        """
        Record audio and save it to a WAV file.

        Returns the path of the saved file.
        """
        audio = self.record(duration, prompt=prompt)
        if filepath is None:
            fd, filepath = tempfile.mkstemp(suffix=".wav")
            os.close(fd)

        pcm = (audio * 32767).astype(np.int16)
        with wave.open(filepath, "wb") as wf:
            wf.setnchannels(self.channels)
            wf.setsampwidth(SAMPLE_WIDTH)
            wf.setframerate(self.sample_rate)
            wf.writeframes(pcm.tobytes())

        return filepath

    def record_multiple(
        self,
        n_samples: int,
        duration: float,
        pause_between: float = 1.5,
    ) -> list[np.ndarray]:
        """
        Record `n_samples` separate audio clips.

        Returns a list of numpy arrays.
        """
        import time

        recordings = []
        for i in range(n_samples):
            prompt = f"  Sample {i + 1}/{n_samples} — speak now"
            audio = self.record(duration, prompt=prompt)
            recordings.append(audio)
            if i < n_samples - 1:
                print(f"  (Pause {pause_between}s before next sample…)")
                time.sleep(pause_between)
        return recordings
