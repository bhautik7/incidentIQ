"""Entry point: ``python -m app``.

Started this way rather than through the ``uvicorn`` CLI so that
``log_config=None`` can be passed - otherwise uvicorn reinstalls its own
logging handlers and overwrites the structlog configuration.
"""

import uvicorn

from app.config import get_settings


def main() -> None:
    settings = get_settings()
    uvicorn.run(
        "app.main:app",
        host=settings.host,
        port=settings.port,
        log_config=None,
        access_log=True,
    )


if __name__ == "__main__":
    main()
