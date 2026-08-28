using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IncidentIQ.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RawLogRetentionWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "raw_log_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitored_service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    log_pattern_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_raw_log_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_raw_log_events_environments_organization_id_environment_id",
                        columns: x => new { x.organization_id, x.environment_id },
                        principalTable: "environments",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_raw_log_events_log_patterns_organization_id_log_pattern_id",
                        columns: x => new { x.organization_id, x.log_pattern_id },
                        principalTable: "log_patterns",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_raw_log_events_monitored_services_organization_id_monitored",
                        columns: x => new { x.organization_id, x.monitored_service_id },
                        principalTable: "monitored_services",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_raw_log_events_organization_id_environment_id",
                table: "raw_log_events",
                columns: new[] { "organization_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_raw_log_events_organization_id_log_pattern_id",
                table: "raw_log_events",
                columns: new[] { "organization_id", "log_pattern_id" });

            migrationBuilder.CreateIndex(
                name: "ix_raw_log_events_organization_id_monitored_service_id",
                table: "raw_log_events",
                columns: new[] { "organization_id", "monitored_service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_raw_log_events_organization_id_occurred_at_id",
                table: "raw_log_events",
                columns: new[] { "organization_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_raw_log_events_organization_id_trace_id",
                table: "raw_log_events",
                columns: new[] { "organization_id", "trace_id" },
                filter: "trace_id IS NOT NULL");

            // Retention deletes by time across every tenant, which the tenant-first
            // btree above cannot serve. BRIN is the right shape for it: the table
            // is append-only and physically ordered by time, so a handful of
            // page-range summaries answer "everything older than X" while costing
            // a rounding error per insert. A btree on occurred_at would cost more
            // to maintain than the nightly delete it exists for.
            migrationBuilder.Sql(
                "CREATE INDEX ix_raw_log_events_occurred_at_brin "
                + "ON raw_log_events USING brin (occurred_at);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "raw_log_events");
        }
    }
}
