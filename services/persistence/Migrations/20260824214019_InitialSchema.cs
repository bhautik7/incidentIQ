using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace IncidentIQ.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    log_retention_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 90),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_events",
                columns: table => new
                {
                    consumer_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_events", x => new { x.consumer_group, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    is_system_role = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "environments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    is_production = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environments", x => x.id);
                    table.UniqueConstraint("ak_environments_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_environments_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "monitored_services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    owner_team = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitored_services", x => x.id);
                    table.UniqueConstraint("ak_monitored_services_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_monitored_services_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.UniqueConstraint("ak_users_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_users_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitored_service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    deployed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    deployed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployments_environments_organization_id_environment_id",
                        columns: x => new { x.organization_id, x.environment_id },
                        principalTable: "environments",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployments_monitored_services_organization_id_monitored_se",
                        columns: x => new { x.organization_id, x.monitored_service_id },
                        principalTable: "monitored_services",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "log_patterns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitored_service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    exception_type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    message_template = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    sample_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    top_stack_frames = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    occurrence_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_muted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_log_patterns", x => x.id);
                    table.UniqueConstraint("ak_log_patterns_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_log_patterns_environments_organization_id_environment_id",
                        columns: x => new { x.organization_id, x.environment_id },
                        principalTable: "environments",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_log_patterns_monitored_services_organization_id_monitored_s",
                        columns: x => new { x.organization_id, x.monitored_service_id },
                        principalTable: "monitored_services",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_logs_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_roles_users_organization_id_user_id",
                        columns: x => new { x.organization_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitored_service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    log_pattern_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurrence_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_notes = table.Column<string>(type: "text", nullable: true),
                    suspected_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.id);
                    table.UniqueConstraint("ak_incidents_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_incidents_deployments_suspected_deployment_id",
                        column: x => x.suspected_deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incidents_environments_organization_id_environment_id",
                        columns: x => new { x.organization_id, x.environment_id },
                        principalTable: "environments",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_incidents_log_patterns_organization_id_log_pattern_id",
                        columns: x => new { x.organization_id, x.log_pattern_id },
                        principalTable: "log_patterns",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_incidents_monitored_services_organization_id_monitored_serv",
                        columns: x => new { x.organization_id, x.monitored_service_id },
                        principalTable: "monitored_services",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    model_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    probable_cause = table.Column<string>(type: "text", nullable: true),
                    suggested_actions = table.Column<string>(type: "jsonb", nullable: true),
                    similar_incidents = table.Column<string>(type: "jsonb", nullable: true),
                    confidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_analyses", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_analyses_incidents_organization_id_incident_id",
                        columns: x => new { x.organization_id, x.incident_id },
                        principalTable: "incidents",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incident_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_events_incidents_organization_id_incident_id",
                        columns: x => new { x.organization_id, x.incident_id },
                        principalTable: "incidents",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_incident_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "log_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitored_service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    log_pattern_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    exception_type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    stack_trace = table.Column<string>(type: "text", nullable: true),
                    trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    span_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    properties = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_log_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_log_events_environments_organization_id_environment_id",
                        columns: x => new { x.organization_id, x.environment_id },
                        principalTable: "environments",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_log_events_incidents_organization_id_incident_id",
                        columns: x => new { x.organization_id, x.incident_id },
                        principalTable: "incidents",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_log_events_log_patterns_organization_id_log_pattern_id",
                        columns: x => new { x.organization_id, x.log_pattern_id },
                        principalTable: "log_patterns",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_log_events_monitored_services_organization_id_monitored_ser",
                        columns: x => new { x.organization_id, x.monitored_service_id },
                        principalTable: "monitored_services",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_analyses_incident_id_analysis_version",
                table: "ai_analyses",
                columns: new[] { "incident_id", "analysis_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_analyses_organization_id_incident_id",
                table: "ai_analyses",
                columns: new[] { "organization_id", "incident_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_user_id",
                table: "audit_logs",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_id_entity_type_entity_id",
                table: "audit_logs",
                columns: new[] { "organization_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_id_occurred_at",
                table: "audit_logs",
                columns: new[] { "organization_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_organization_id_environment_id",
                table: "deployments",
                columns: new[] { "organization_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_organization_id_monitored_service_id_environmen",
                table: "deployments",
                columns: new[] { "organization_id", "monitored_service_id", "environment_id", "deployed_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_environments_organization_id_key",
                table: "environments",
                columns: new[] { "organization_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_events_actor_user_id",
                table: "incident_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_events_organization_id_incident_id_occurred_at",
                table: "incident_events",
                columns: new[] { "organization_id", "incident_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_organization_id_environment_id",
                table: "incidents",
                columns: new[] { "organization_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_organization_id_monitored_service_id",
                table: "incidents",
                columns: new[] { "organization_id", "monitored_service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_organization_id_status_last_seen_at",
                table: "incidents",
                columns: new[] { "organization_id", "status", "last_seen_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_suspected_deployment_id",
                table: "incidents",
                column: "suspected_deployment_id");

            migrationBuilder.CreateIndex(
                name: "ux_incidents_active_pattern",
                table: "incidents",
                columns: new[] { "organization_id", "log_pattern_id" },
                unique: true,
                filter: "status IN ('Open', 'Acknowledged')");

            migrationBuilder.CreateIndex(
                name: "ix_log_events_incident_id_occurred_at",
                table: "log_events",
                columns: new[] { "incident_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "incident_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_log_events_organization_id_environment_id",
                table: "log_events",
                columns: new[] { "organization_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_log_events_organization_id_event_id",
                table: "log_events",
                columns: new[] { "organization_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_log_events_organization_id_incident_id",
                table: "log_events",
                columns: new[] { "organization_id", "incident_id" });

            migrationBuilder.CreateIndex(
                name: "ix_log_events_organization_id_log_pattern_id_occurred_at",
                table: "log_events",
                columns: new[] { "organization_id", "log_pattern_id", "occurred_at" },
                descending: new[] { false, false, true },
                filter: "log_pattern_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_log_events_organization_id_monitored_service_id",
                table: "log_events",
                columns: new[] { "organization_id", "monitored_service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_log_events_organization_id_trace_id",
                table: "log_events",
                columns: new[] { "organization_id", "trace_id" },
                filter: "trace_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_log_patterns_organization_id_environment_id",
                table: "log_patterns",
                columns: new[] { "organization_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_log_patterns_organization_id_fingerprint",
                table: "log_patterns",
                columns: new[] { "organization_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_log_patterns_organization_id_monitored_service_id_environme",
                table: "log_patterns",
                columns: new[] { "organization_id", "monitored_service_id", "environment_id", "last_seen_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_monitored_services_organization_id_key",
                table: "monitored_services",
                columns: new[] { "organization_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_processed_events_expires_at",
                table: "processed_events",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_organization_id_user_id",
                table: "user_roles",
                columns: new[] { "organization_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_organization_id_email",
                table: "users",
                columns: new[] { "organization_id", "email" },
                unique: true);

            // ---------------------------------------------------------------
            // PostgreSQL's default ON DELETE SET NULL nulls *every* referencing
            // column. On these two composite foreign keys that includes
            // organization_id, which is NOT NULL, so deleting an incident or a
            // log pattern would fail with a not-null violation.
            //
            // PostgreSQL 15+ can restrict SET NULL to a subset of columns.
            // EF Core has no way to express that, so the constraints are
            // redefined here.
            // ---------------------------------------------------------------
            migrationBuilder.Sql("""
                ALTER TABLE log_events
                    DROP CONSTRAINT fk_log_events_incidents_organization_id_incident_id;

                ALTER TABLE log_events
                    ADD CONSTRAINT fk_log_events_incidents_organization_id_incident_id
                    FOREIGN KEY (organization_id, incident_id)
                    REFERENCES incidents (organization_id, id)
                    ON DELETE SET NULL (incident_id);

                ALTER TABLE log_events
                    DROP CONSTRAINT fk_log_events_log_patterns_organization_id_log_pattern_id;

                ALTER TABLE log_events
                    ADD CONSTRAINT fk_log_events_log_patterns_organization_id_log_pattern_id
                    FOREIGN KEY (organization_id, log_pattern_id)
                    REFERENCES log_patterns (organization_id, id)
                    ON DELETE SET NULL (log_pattern_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analyses");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "incident_events");

            migrationBuilder.DropTable(
                name: "log_events");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "processed_events");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "log_patterns");

            migrationBuilder.DropTable(
                name: "environments");

            migrationBuilder.DropTable(
                name: "monitored_services");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
