"""How unusual is this, compared with itself?

This is the step that separates a *repeated error pattern* from an *anomaly*.
A pattern occurring 200 times a minute is not remarkable if it has always
occurred 200 times a minute; the same pattern going from 2 to 200 is.
Answering that needs a baseline, which is what the minute buckets provide.

Two independent methods, on purpose:

- A **robust z-score** built on the median and the median absolute deviation.
  Interpretable, and resistant to the outlier it is measuring - a plain mean
  and standard deviation get dragged upwards by the very spike being scored,
  which quietly makes large spikes look ordinary.
- **scikit-learn's IsolationForest**, which decides outlier-ness from the shape
  of the distribution rather than from an assumed one. It disagrees with the
  z-score often enough to be worth having, particularly on bursty patterns
  where "normal" is not a single number.

Neither is a model of the system. Both are descriptions of a series.
"""

import numpy as np
import structlog
from sklearn.ensemble import IsolationForest

from app.analysis.evidence import AnomalyEvidence

logger = structlog.get_logger(__name__)

#: Below this many baseline buckets there is no distribution to speak of, and
#: any "anomaly" is an artefact of a short history rather than a finding.
MIN_BASELINE_SAMPLES = 5

#: 0.6745 converts a median absolute deviation into a standard-deviation
#: equivalent for normally distributed data, so the z-score reads on the usual
#: scale where 3 is notable.
MAD_TO_SIGMA = 0.6745


def analyse(
    *,
    window_counts: list[int],
    baseline_counts: list[int],
    window_minutes: int,
) -> AnomalyEvidence:
    """Scores the current window against the baseline buckets."""
    window_total = int(sum(window_counts))
    window_rate = window_total / max(window_minutes, 1)

    baseline = np.asarray(baseline_counts, dtype=np.float64)

    if baseline.size < MIN_BASELINE_SAMPLES:
        # Not enough history to call anything unusual. Reported honestly rather
        # than dressed up as a confident zero.
        return AnomalyEvidence(
            window_count=window_total,
            baseline_mean_per_minute=float(baseline.mean()) if baseline.size else 0.0,
            window_rate_per_minute=window_rate,
            magnitude=0.0,
            robust_z_score=0.0,
            is_outlier=False,
            outlier_score=0.0,
            baseline_sample_count=int(baseline.size),
        )

    baseline_mean = float(baseline.mean())
    median = float(np.median(baseline))
    mad = float(np.median(np.abs(baseline - median)))

    # A perfectly flat baseline has zero deviation, which would divide by zero.
    # Falling back to a half-count floor keeps the score finite and still
    # registers a genuine jump.
    scale = mad / MAD_TO_SIGMA if mad > 0 else 0.5
    robust_z = (window_rate - median) / scale

    magnitude = window_rate / baseline_mean if baseline_mean > 0 else float(window_rate)

    is_outlier, outlier_score = _isolation_forest_verdict(baseline, window_rate)

    return AnomalyEvidence(
        window_count=window_total,
        baseline_mean_per_minute=baseline_mean,
        window_rate_per_minute=window_rate,
        magnitude=round(magnitude, 2),
        robust_z_score=round(robust_z, 2),
        is_outlier=is_outlier,
        outlier_score=round(outlier_score, 4),
        baseline_sample_count=int(baseline.size),
    )


def _isolation_forest_verdict(baseline: np.ndarray, window_rate: float) -> tuple[bool, float]:
    """Fits on the baseline alone, then scores the current window against it.

    Fitting on the baseline *excluding* the window matters: including the point
    being judged teaches the model that the spike is normal, which is precisely
    backwards.
    """
    try:
        forest = IsolationForest(
            n_estimators=100,
            contamination="auto",
            random_state=0,  # Reproducible: the same series must score the same twice.
        )
        forest.fit(baseline.reshape(-1, 1))

        score = float(forest.decision_function(np.array([[window_rate]]))[0])
        prediction = int(forest.predict(np.array([[window_rate]]))[0])

        # IsolationForest returns -1 for outliers. A rate *below* baseline is
        # also an outlier statistically, but a service that suddenly goes quiet
        # is not what this system is looking for.
        return prediction == -1 and window_rate > float(np.median(baseline)), score
    except Exception:  # noqa: BLE001
        # A degraded anomaly score must never cost the rest of the analysis.
        logger.warning("isolation_forest_failed", exc_info=True)
        return False, 0.0


def describe(evidence: AnomalyEvidence) -> str:
    """One sentence a human can act on."""
    if evidence.baseline_sample_count < MIN_BASELINE_SAMPLES:
        return "Not enough history to say whether this rate is unusual."

    if evidence.magnitude >= 2.0:
        return (
            f"{evidence.window_rate_per_minute:.1f}/min against a baseline of "
            f"{evidence.baseline_mean_per_minute:.2f}/min - {evidence.magnitude:.1f}x normal "
            f"({evidence.robust_z_score:.1f} MAD above the median)."
        )

    return (
        f"{evidence.window_rate_per_minute:.1f}/min is close to the baseline of "
        f"{evidence.baseline_mean_per_minute:.2f}/min."
    )
