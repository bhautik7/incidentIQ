"""Structured logging that matches the .NET services.

Everything - our own log calls, uvicorn's, and any library's - is routed
through a single structlog formatter, so one container never emits two log
formats. Each line carries ``service``, ``version`` and ``environment``, the
same enrichers the Serilog configuration applies on the .NET side.
"""

import logging
import sys

import structlog

from app import __version__
from app.config import Settings

_UVICORN_LOGGERS = ("uvicorn", "uvicorn.error", "uvicorn.access")


def configure_logging(settings: Settings) -> None:
    level = getattr(logging, settings.log_level.upper(), logging.INFO)

    renderer = (
        structlog.processors.JSONRenderer()
        if settings.log_format.lower() == "json"
        else structlog.dev.ConsoleRenderer()
    )

    def add_service_context(_logger: object, _name: str, event_dict: dict) -> dict:
        """Stamp service identity on every record.

        This is a processor rather than bound contextvars because uvicorn
        handles requests in its own asyncio contexts, which would not inherit
        values bound at startup - access-log lines would lose the fields.
        """
        event_dict.setdefault("service", settings.service_name)
        event_dict.setdefault("version", __version__)
        event_dict.setdefault("environment", settings.environment)
        return event_dict

    # Applied both to structlog calls and to plain stdlib records coming from
    # third-party libraries, which is what keeps the output uniform.
    shared_processors: list[structlog.typing.Processor] = [
        structlog.contextvars.merge_contextvars,
        add_service_context,
        structlog.stdlib.add_log_level,
        structlog.stdlib.add_logger_name,
        structlog.processors.TimeStamper(fmt="iso", utc=True),
        structlog.processors.StackInfoRenderer(),
        structlog.processors.format_exc_info,
    ]

    structlog.configure(
        processors=[*shared_processors, structlog.stdlib.ProcessorFormatter.wrap_for_formatter],
        logger_factory=structlog.stdlib.LoggerFactory(),
        wrapper_class=structlog.stdlib.BoundLogger,
        cache_logger_on_first_use=True,
    )

    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(
        structlog.stdlib.ProcessorFormatter(
            foreign_pre_chain=shared_processors,
            processors=[structlog.stdlib.ProcessorFormatter.remove_processors_meta, renderer],
        )
    )

    root = logging.getLogger()
    root.handlers.clear()
    root.addHandler(handler)
    root.setLevel(level)

    # Uvicorn installs its own handlers; drop them so its records propagate to
    # the root handler above instead of being printed in uvicorn's own format.
    for name in _UVICORN_LOGGERS:
        uvicorn_logger = logging.getLogger(name)
        uvicorn_logger.handlers.clear()
        uvicorn_logger.propagate = True
