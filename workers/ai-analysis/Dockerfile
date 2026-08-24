# syntax=docker/dockerfile:1
#
# IncidentIQ AI analysis worker (FastAPI).
# Build context is this directory:
#   docker build -f workers/ai-analysis/Dockerfile workers/ai-analysis

FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

# curl is only here so the container HEALTHCHECK below has something to call.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Dependencies first so the layer survives source changes.
COPY requirements.txt ./
RUN pip install --requirement requirements.txt

COPY app/ ./app/

# Run as a non-root user.
RUN useradd --create-home --uid 10001 appuser && chown -R appuser:appuser /app
USER appuser

ENV HOST=0.0.0.0 \
    PORT=8000

EXPOSE 8000

HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
    CMD curl --fail --silent --show-error http://localhost:8000/health/live || exit 1

CMD ["python", "-m", "app"]
