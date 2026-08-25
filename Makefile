# IncidentIQ developer entry points.
# Everything here is a thin wrapper around docker compose / dotnet / npm so
# that no one has to remember the flag combinations.

COMPOSE := docker compose -f infrastructure/docker/docker-compose.yml
WEB     := apps/web
AI      := workers/ai-analysis

.DEFAULT_GOAL := help
.PHONY: help env up down build logs ps init health clean \
        dotnet-build dotnet-test web-build web-dev ai-test verify

help: ## Show this help
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
	  | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}'

env: ## Create infrastructure/docker/.env from the example (never overwrites)
	@test -f infrastructure/docker/.env \
	  || (cp infrastructure/docker/.env.example infrastructure/docker/.env && echo "created infrastructure/docker/.env")

up: env ## Build and start the whole stack
	$(COMPOSE) up -d --build
	$(MAKE) init

down: ## Stop the stack (keeps volumes)
	$(COMPOSE) down

clean: ## Stop the stack and delete all volumes
	$(COMPOSE) down --volumes

build: env ## Build the application images only
	$(COMPOSE) build api ingestion event-processor ai-analysis web

init: ## Create Kafka topics and the MinIO bucket (idempotent)
	$(COMPOSE) run --rm kafka-init
	$(COMPOSE) run --rm minio-init

ps: ## Show container status
	$(COMPOSE) ps

logs: ## Tail logs for the application services
	$(COMPOSE) logs -f api ingestion event-processor ai-analysis web

health: ## Curl every service's health endpoints
	@./scripts/check-health.sh

dotnet-build: ## Compile the .NET solution
	dotnet build IncidentIQ.slnx

dotnet-test: ## Run the .NET tests
	dotnet test IncidentIQ.slnx

web-build: ## Type-check and build the frontend
	cd $(WEB) && npm ci && npm run build

web-dev: ## Run the Vite dev server on :5173
	cd $(WEB) && npm run dev

ai-test: ## Lint and test the Python worker
	cd $(AI) && .venv/bin/ruff check app tests && .venv/bin/python -m pytest -q

verify: dotnet-build dotnet-test web-build ## Build and test everything that does not need Docker

# ---- database ----------------------------------------------------------
PERSISTENCE := services/persistence/IncidentIQ.Persistence.csproj
MIGRATIONS_CONN ?= Host=localhost;Port=5433;Database=incidentiq;Username=incidentiq;Password=$(POSTGRES_PASSWORD)

.PHONY: db-migrate db-script db-reset db-add-migration db-test

db-migrate: ## Apply migrations to the local database
	INCIDENTIQ_MIGRATIONS_CONNECTION="$(MIGRATIONS_CONN)" dotnet ef database update --project $(PERSISTENCE)

db-script: ## Print the full schema as SQL (review this before deploying)
	dotnet ef migrations script --idempotent --project $(PERSISTENCE)

db-add-migration: ## make db-add-migration NAME=AddSomething
	@test -n "$(NAME)" || (echo "usage: make db-add-migration NAME=AddSomething" && exit 1)
	dotnet ef migrations add $(NAME) --project $(PERSISTENCE) --output-dir Migrations

db-reset: ## Drop and recreate the local schema, then re-seed via the API
	INCIDENTIQ_MIGRATIONS_CONNECTION="$(MIGRATIONS_CONN)" dotnet ef database update 0 --project $(PERSISTENCE)
	INCIDENTIQ_MIGRATIONS_CONNECTION="$(MIGRATIONS_CONN)" dotnet ef database update --project $(PERSISTENCE)
	$(COMPOSE) restart api

db-test: ## Run the persistence integration tests (needs Docker for Testcontainers)
	dotnet test tests/IncidentIQ.Persistence.Tests/IncidentIQ.Persistence.Tests.csproj

# ---- contracts ---------------------------------------------------------
.PHONY: contracts-samples contracts-test

contracts-samples: ## Regenerate contracts/samples/*.json from the C# types
	dotnet run --project tools/contract-samples -- contracts/samples

contracts-test: ## Verify both languages agree on the wire format
	dotnet test tests/IncidentIQ.Messaging.Tests/IncidentIQ.Messaging.Tests.csproj
	cd $(AI) && .venv/bin/python -m pytest -q tests/test_contracts.py
