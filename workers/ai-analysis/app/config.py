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


    @property
    def cors_origins(self) -> list[str]:
        """Comma-separated list, mirroring Cors__AllowedOrigins on the .NET side."""
        return [origin.strip() for origin in self.cors_allowed_origins.split(",") if origin.strip()]


@lru_cache
def get_settings() -> Settings:
    return Settings()
