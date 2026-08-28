from fastapi.testclient import TestClient

from app.main import app


def test_liveness_is_healthy_without_any_dependency() -> None:
    with TestClient(app) as client:
        response = client.get("/health/live")

    assert response.status_code == 200
    assert response.json()["status"] == "Healthy"


def test_readiness_names_the_dependencies_it_cannot_reach() -> None:
    with TestClient(app) as client:
        response = client.get("/health/ready")

    # Nothing is configured in the test process, so readiness must fail and say why.
    assert response.status_code == 503

    checks = {check["name"]: check for check in response.json()["checks"]}
    assert set(checks) == {"postgres", "kafka", "kafka-consumer"}

    # The two unreachable dependencies are what make this 503.
    assert checks["postgres"]["status"] == "Unhealthy"
    assert checks["kafka"]["status"] == "Unhealthy"

    # The consumer check is not one of them: no consumer has started in this
    # process, and reporting that as a fault would fail readiness on every
    # container during boot, before it has had a chance to subscribe.
    assert checks["kafka-consumer"]["status"] == "Healthy"


def test_root_identifies_the_service() -> None:
    with TestClient(app) as client:
        body = client.get("/").json()

    assert body["service"] == "incidentiq-ai-analysis"
    assert body["status"] == "running"
