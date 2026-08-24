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
    names = {check["name"] for check in response.json()["checks"]}
    assert names == {"postgres", "kafka"}


def test_root_identifies_the_service() -> None:
    with TestClient(app) as client:
        body = client.get("/").json()

    assert body["service"] == "incidentiq-ai-analysis"
    assert body["status"] == "running"
