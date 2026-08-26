"""The anomaly scorer, which is what separates a repeated pattern from an anomaly."""

from app.analysis import anomaly


def test_a_flat_baseline_with_a_sharp_spike_scores_as_anomalous():
    # 2/min for three hours, then 40/min. The definition of a regression.
    result = anomaly.analyse(
        window_counts=[40, 40, 40, 40, 40],
        baseline_counts=[2] * 60,
        window_minutes=5,
    )

    assert result.magnitude == 20.0
    assert result.robust_z_score > 3
    assert result.window_rate_per_minute == 40.0


def test_a_steady_pattern_is_not_anomalous_however_loud_it_is():
    # 200/min forever is not a finding. A tool that pages on volume rather than
    # on change is a tool people mute.
    result = anomaly.analyse(
        window_counts=[200] * 5,
        baseline_counts=[200] * 60,
        window_minutes=5,
    )

    assert result.magnitude == 1.0
    assert not result.is_outlier


def test_too_little_history_reports_honestly_rather_than_confidently():
    result = anomaly.analyse(window_counts=[50], baseline_counts=[1, 2], window_minutes=5)

    assert result.baseline_sample_count == 2
    assert result.magnitude == 0.0
    assert not result.is_outlier
    assert "not enough history" in anomaly.describe(result).lower()


def test_a_zero_variance_baseline_does_not_divide_by_zero():
    # A perfectly flat baseline has a median absolute deviation of zero.
    result = anomaly.analyse(
        window_counts=[30] * 5,
        baseline_counts=[5] * 40,
        window_minutes=5,
    )

    assert result.robust_z_score > 0
    assert result.robust_z_score != float("inf")


def test_a_drop_in_rate_is_not_reported_as_an_incident_worthy_outlier():
    # Statistically an outlier; operationally not what this system looks for.
    result = anomaly.analyse(
        window_counts=[0, 0, 0, 0, 1],
        baseline_counts=[50] * 60,
        window_minutes=5,
    )

    assert not result.is_outlier


def test_isolation_forest_agrees_with_the_z_score_on_an_obvious_spike():
    result = anomaly.analyse(
        window_counts=[100] * 5,
        baseline_counts=[1, 2, 1, 3, 2, 1, 2, 2, 1, 3] * 6,
        window_minutes=5,
    )

    assert result.is_outlier
    assert result.magnitude > 10


def test_scoring_is_reproducible():
    # A fixed seed matters: the same series must score identically twice, or
    # the same incident gets different explanations on a retry.
    args = {"window_counts": [40] * 5, "baseline_counts": [2] * 60, "window_minutes": 5}

    assert anomaly.analyse(**args) == anomaly.analyse(**args)


def test_describe_names_the_numbers_a_human_would_check():
    result = anomaly.analyse(
        window_counts=[40] * 5, baseline_counts=[2] * 60, window_minutes=5
    )
    described = anomaly.describe(result)

    assert "40.0/min" in described
    assert "20.0x normal" in described
