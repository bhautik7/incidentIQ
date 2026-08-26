"""Configuration, sourced entirely from environment variables.

Nothing here has a secret as a default. Anything that would carry a
credential (the PostgreSQL DSN) defaults to ``None`` and the corresponding
readiness check reports which variable is missing.
"""

from functools import lru_cache

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(extra="ignore", case_sensitive=False)

    service_name: str = Field(default="incidentiq-ai-analysis", alias="SERVICE_NAME")
    environment: str = Field(default="Development", alias="ENVIRONMENT")

    host: str = Field(default="0.0.0.0", alias="HOST")
    port: int = Field(default=8000, alias="PORT")

    log_level: str = Field(default="INFO", alias="LOG_LEVEL")
    log_format: str = Field(default="json", alias="LOG_FORMAT")

    cors_allowed_origins: str = Field(default="", alias="CORS_ALLOWED_ORIGINS")

    postgres_dsn: str | None = Field(default=None, alias="POSTGRES_DSN")
    kafka_bootstrap_servers: str | None = Field(default=None, alias="KAFKA_BOOTSTRAP_SERVERS")

    # ---- Analysis worker ----

    analysis_enabled: bool = Field(default=True, alias="ANALYSIS_ENABLED")
    kafka_consumer_group: str = Field(default="ai-enricher", alias="KAFKA_CONSUMER_GROUP")

    #: Local sentence-transformers model. Chosen for size and speed rather than
    #: peak quality: 22M parameters, ~90MB, runs on CPU in single-digit
    #: milliseconds, and needs no API key and no network at inference time.
    embedding_model: str = Field(default="sentence-transformers/all-MiniLM-L6-v2", alias="EMBEDDING_MODEL")

    #: Must equal the model's output width and the vector(N) column. Not a
    #: preference - a mismatch fails every insert.
    embedding_dimensions: int = Field(default=384, alias="EMBEDDING_DIMENSIONS")

    #: How many historical incidents the similarity search returns.
    similarity_top_k: int = Field(default=5, alias="SIMILARITY_TOP_K")

    #: Below this cosine similarity a match is noise. Showing a 0.2-similar
    #: incident as "related" is worse than showing nothing, because it teaches
    #: people to ignore the section.
    similarity_min_score: float = Field(default=0.55, alias="SIMILARITY_MIN_SCORE")

    #: How far back to look for a deployment that could explain an incident.
    deployment_correlation_minutes: int = Field(default=60, alias="DEPLOYMENT_CORRELATION_MINUTES")

    #: Minute buckets used as the anomaly baseline.
    anomaly_baseline_minutes: int = Field(default=180, alias="ANOMALY_BASELINE_MINUTES")

    #: Detection window, matching the .NET detector so both describe the same span.
    anomaly_window_minutes: int = Field(default=5, alias="ANOMALY_WINDOW_MINUTES")


    @property
    def cors_origins(self) -> list[str]:
        """Comma-separated list, mirroring Cors__AllowedOrigins on the .NET side."""
        return [origin.strip() for origin in self.cors_allowed_origins.split(",") if origin.strip()]


@lru_cache
def get_settings() -> Settings:
    return Settings()
