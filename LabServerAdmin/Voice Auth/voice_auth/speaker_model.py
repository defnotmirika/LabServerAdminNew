"""
speaker_model.py
----------------
Trains and scores a Gaussian Mixture Model (GMM) for a single speaker.

Each enrolled speaker gets their own GMM trained on their MFCC features.
During authentication, the GMM returns a log-likelihood score — the higher,
the more likely the audio matches that speaker.
"""

import numpy as np
from sklearn.mixture import GaussianMixture


N_COMPONENTS = 16
COVARIANCE_TYPE = "diag"
MAX_ITER = 200
RANDOM_STATE = 42


class SpeakerModel:
    """GMM-based speaker voice model."""

    def __init__(
        self,
        n_components: int = N_COMPONENTS,
        covariance_type: str = COVARIANCE_TYPE,
    ):
        self.n_components = n_components
        self.covariance_type = covariance_type
        self.gmm: GaussianMixture | None = None
        self.is_trained = False

    def train(self, features: np.ndarray) -> None:
        """
        Train the GMM on a matrix of feature vectors.

        Parameters
        ----------
        features : np.ndarray of shape (n_frames, n_features)
        """
        n_samples = len(features)
        n_components = min(self.n_components, max(1, n_samples // 10))

        self.gmm = GaussianMixture(
            n_components=n_components,
            covariance_type=self.covariance_type,
            max_iter=MAX_ITER,
            random_state=RANDOM_STATE,
            n_init=3,
            reg_covar=1e-3,
        )
        self.gmm.fit(features)
        self.is_trained = True

    def score(self, features: np.ndarray) -> float:
        """
        Compute the average log-likelihood of `features` under this model.

        Returns -inf if the model is not trained.
        """
        if not self.is_trained or self.gmm is None:
            return float("-inf")
        return float(self.gmm.score(features))

    def to_dict(self) -> dict:
        """Serialise the model to a plain dict for persistence."""
        if not self.is_trained or self.gmm is None:
            raise RuntimeError("Model is not trained yet.")
        return {
            "n_components": self.gmm.n_components,
            "covariance_type": self.gmm.covariance_type,
            "weights": self.gmm.weights_.tolist(),
            "means": self.gmm.means_.tolist(),
            "covariances": self.gmm.covariances_.tolist(),
            "precisions_chol": self.gmm.precisions_cholesky_.tolist(),
        }

    @classmethod
    def from_dict(cls, data: dict) -> "SpeakerModel":
        """Restore a SpeakerModel from a previously serialised dict."""
        model = cls(
            n_components=data["n_components"],
            covariance_type=data["covariance_type"],
        )
        gmm = GaussianMixture(
            n_components=data["n_components"],
            covariance_type=data["covariance_type"],
        )
        gmm.weights_ = np.array(data["weights"])
        gmm.means_ = np.array(data["means"])
        gmm.covariances_ = np.array(data["covariances"])
        gmm.precisions_cholesky_ = np.array(data["precisions_chol"])

        import sklearn.mixture._gaussian_mixture as _gm
        gmm.precisions_ = _gm._compute_precision_cholesky(
            gmm.covariances_, data["covariance_type"]
        )

        gmm.converged_ = True
        gmm.n_iter_ = 0
        gmm.lower_bound_ = 0.0
        gmm.n_features_in_ = gmm.means_.shape[1]
        model.gmm = gmm
        model.is_trained = True
        return model
