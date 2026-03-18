"""
feature_extractor.py
--------------------
Extracts MFCC-based features from raw audio for speaker verification.

The pipeline:
  1. Pre-emphasis filtering
  2. Silence / low-energy frame removal
  3. MFCC extraction (+ delta + delta-delta)
  4. Mean normalisation (cepstral mean subtraction)
"""

import numpy as np
from typing import Optional


N_MFCC = 20
N_MFCC_TOTAL = N_MFCC * 3
SAMPLE_RATE = 16000
FRAME_LENGTH = 0.025
FRAME_STEP = 0.010
N_FFT = 512
N_MELS = 40
ENERGY_THRESHOLD = 0.005


def _preemphasis(signal: np.ndarray, coeff: float = 0.97) -> np.ndarray:
    """Apply pre-emphasis filter to boost high frequencies."""
    return np.append(signal[0], signal[1:] - coeff * signal[:-1])


def _remove_silence(
    frames: np.ndarray, threshold: float = ENERGY_THRESHOLD
) -> np.ndarray:
    """Remove low-energy (silent) frames."""
    energies = np.sum(frames**2, axis=1)
    mask = energies > threshold
    voiced = frames[mask]
    if len(voiced) < 5:
        return frames
    return voiced


def _hz_to_mel(hz: float) -> float:
    return 2595.0 * np.log10(1.0 + hz / 700.0)


def _mel_to_hz(mel: float) -> float:
    return 700.0 * (10.0 ** (mel / 2595.0) - 1.0)


def _mel_filterbank(n_filters: int, n_fft: int, sample_rate: int) -> np.ndarray:
    low_mel = _hz_to_mel(0)
    high_mel = _hz_to_mel(sample_rate / 2)
    mel_points = np.linspace(low_mel, high_mel, n_filters + 2)
    hz_points = np.array([_mel_to_hz(m) for m in mel_points])
    bin_points = np.floor((n_fft + 1) * hz_points / sample_rate).astype(int)

    fbank = np.zeros((n_filters, int(np.floor(n_fft / 2 + 1))))
    for m in range(1, n_filters + 1):
        f_m_minus = bin_points[m - 1]
        f_m = bin_points[m]
        f_m_plus = bin_points[m + 1]
        for k in range(f_m_minus, f_m):
            fbank[m - 1, k] = (k - bin_points[m - 1]) / (bin_points[m] - bin_points[m - 1])
        for k in range(f_m, f_m_plus):
            fbank[m - 1, k] = (bin_points[m + 1] - k) / (bin_points[m + 1] - bin_points[m])
    return fbank


try:
    import librosa

    def extract_mfcc(
        audio: np.ndarray,
        sample_rate: int = SAMPLE_RATE,
        n_mfcc: int = N_MFCC,
    ) -> Optional[np.ndarray]:
        """Extract MFCC + delta + delta-delta features using librosa."""
        if len(audio) == 0:
            return None

        audio = audio.astype(np.float32)
        if np.max(np.abs(audio)) > 0:
            audio = audio / np.max(np.abs(audio))

        mfcc = librosa.feature.mfcc(
            y=audio,
            sr=sample_rate,
            n_mfcc=n_mfcc,
            n_fft=N_FFT,
            hop_length=int(sample_rate * FRAME_STEP),
            win_length=int(sample_rate * FRAME_LENGTH),
            n_mels=N_MELS,
        )
        delta = librosa.feature.delta(mfcc)
        delta2 = librosa.feature.delta(mfcc, order=2)

        features = np.vstack([mfcc, delta, delta2]).T
        features -= features.mean(axis=0)
        return features

except ImportError:

    def _compute_mfcc_numpy(
        audio: np.ndarray,
        sample_rate: int,
        n_mfcc: int,
    ) -> np.ndarray:
        """Fallback pure-numpy MFCC computation."""
        audio = _preemphasis(audio)
        frame_len = int(sample_rate * FRAME_LENGTH)
        frame_step = int(sample_rate * FRAME_STEP)

        n_frames = 1 + (len(audio) - frame_len) // frame_step
        indices = (
            np.tile(np.arange(0, frame_len), (n_frames, 1))
            + np.tile(np.arange(0, n_frames * frame_step, frame_step), (frame_len, 1)).T
        )
        frames = audio[indices.astype(np.int32)]
        frames *= np.hamming(frame_len)

        mag = np.absolute(np.fft.rfft(frames, N_FFT))
        pow_spec = (1.0 / N_FFT) * (mag**2)

        fbank = _mel_filterbank(N_MELS, N_FFT, sample_rate)
        filter_banks = np.dot(pow_spec, fbank.T)
        filter_banks = np.where(filter_banks == 0, np.finfo(float).eps, filter_banks)
        filter_banks = 20 * np.log10(filter_banks)

        num_ceps = n_mfcc
        mfcc = np.dot(filter_banks, np.cos(np.pi * np.arange(1, num_ceps + 1) * np.arange(0.5, N_MELS + 0.5)[:, None] / N_MELS))

        return mfcc

    def _delta(features: np.ndarray, n: int = 2) -> np.ndarray:
        """Compute delta features."""
        denom = 2 * sum(i**2 for i in range(1, n + 1))
        pad = np.tile(features[0], (n, 1))
        pad_end = np.tile(features[-1], (n, 1))
        padded = np.concatenate([pad, features, pad_end])
        delta = np.zeros_like(features)
        for t in range(len(features)):
            delta[t] = sum(i * (padded[t + n + i] - padded[t + n - i]) for i in range(1, n + 1)) / denom
        return delta

    def extract_mfcc(
        audio: np.ndarray,
        sample_rate: int = SAMPLE_RATE,
        n_mfcc: int = N_MFCC,
    ) -> Optional[np.ndarray]:
        """Extract MFCC + delta + delta-delta (numpy fallback)."""
        if len(audio) == 0:
            return None
        audio = audio.astype(np.float32)
        if np.max(np.abs(audio)) > 0:
            audio = audio / np.max(np.abs(audio))

        mfcc = _compute_mfcc_numpy(audio, sample_rate, n_mfcc)
        delta = _delta(mfcc)
        delta2 = _delta(delta)
        features = np.hstack([mfcc, delta, delta2])
        features -= features.mean(axis=0)
        return features
