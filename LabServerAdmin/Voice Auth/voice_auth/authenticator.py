"""
authenticator.py
----------------
Speaker authentication and identification logic.

- authenticate(name, features)  → True/False  (1:1 verification)
- identify(features)            → best_match name or None (1:N identification)

Authentication uses a threshold on the difference between the claimed
speaker's GMM score and the best impostor score (normalised log-likelihood).
"""

import numpy as np
from voice_auth.speaker_model import SpeakerModel
from voice_auth.profile_manager import load_profile, load_all_profiles


THRESHOLD = -2.5


class AuthenticationResult:
    def __init__(
        self,
        accepted: bool,
        claimed_name: str | None,
        matched_name: str | None,
        score: float,
        threshold: float,
        all_scores: dict[str, float] | None = None,
    ):
        self.accepted = accepted
        self.claimed_name = claimed_name
        self.matched_name = matched_name
        self.score = score
        self.threshold = threshold
        self.all_scores = all_scores or {}

    def __repr__(self):
        status = "ACCEPTED" if self.accepted else "REJECTED"
        return (
            f"AuthenticationResult(status={status}, "
            f"claimed={self.claimed_name}, matched={self.matched_name}, "
            f"score={self.score:.4f})"
        )


def authenticate(
    name: str,
    features: np.ndarray,
    threshold: float = THRESHOLD,
) -> AuthenticationResult:
    """
    1:1 Speaker verification.

    Checks whether `features` match the enrolled profile for `name`.
    """
    result = load_profile(name)
    if result is None:
        return AuthenticationResult(
            accepted=False,
            claimed_name=name,
            matched_name=None,
            score=float("-inf"),
            threshold=threshold,
        )
    display_name, model = result
    score = model.score(features)
    accepted = score >= threshold

    return AuthenticationResult(
        accepted=accepted,
        claimed_name=name,
        matched_name=display_name if accepted else None,
        score=score,
        threshold=threshold,
    )


def identify(
    features: np.ndarray,
    threshold: float = THRESHOLD,
) -> AuthenticationResult:
    """
    1:N Speaker identification.

    Scores `features` against all enrolled speakers and returns the best match
    if its score exceeds `threshold`.
    """
    all_models = load_all_profiles()
    if not all_models:
        return AuthenticationResult(
            accepted=False,
            claimed_name=None,
            matched_name=None,
            score=float("-inf"),
            threshold=threshold,
        )

    scores = {name: model.score(features) for name, model in all_models.items()}
    best_name = max(scores, key=lambda k: scores[k])
    best_score = scores[best_name]
    accepted = best_score >= threshold

    return AuthenticationResult(
        accepted=accepted,
        claimed_name=None,
        matched_name=best_name if accepted else None,
        score=best_score,
        threshold=threshold,
        all_scores=scores,
    )
