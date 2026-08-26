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
#
# torch is installed from PyTorch's CPU index before anything else asks for it.
# The default PyPI wheel bundles CUDA and is several gigabytes; this worker runs
# embeddings on CPU and never touches a GPU, so the CUDA build is pure weight.
COPY requirements.txt ./
RUN pip install --index-url https://download.pytorch.org/whl/cpu torch==2.9.1 \
    && pip install --requirement requirements.txt

# Bake the model into the image.
#
# Without this the first message to arrive triggers a ~90MB download, which
# makes the first analysis slow, ties startup to Hugging Face being reachable,
# and means an air-gapped deployment silently never works.
ENV HF_HOME=/opt/huggingface \
    SENTENCE_TRANSFORMERS_HOME=/opt/huggingface \
    HF_HUB_DISABLE_TELEMETRY=1
ARG EMBEDDING_MODEL=sentence-transformers/all-MiniLM-L6-v2
RUN python -c "from sentence_transformers import SentenceTransformer; SentenceTransformer('${EMBEDDING_MODEL}')" \
    && chmod -R a+rX /opt/huggingface

COPY app/ ./app/

# Run as a non-root user.
RUN useradd --create-home --uid 10001 appuser && chown -R appuser:appuser /app
USER appuser

ENV HOST=0.0.0.0 \
    PORT=8000

EXPOSE 8000

HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=5 \
    CMD curl --fail --silent --show-error http://localhost:8000/health/live || exit 1

CMD ["python", "-m", "app"]
