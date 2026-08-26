using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentIQ.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IncidentDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------
            // Existing rows predate this migration: they carry the old status
            // vocabulary, have no dedupe key, and no detection rule. All three
            // are fixed here rather than left to defaults, because the new
            // partial index names the new statuses and an empty dedupe key
            // would collapse every legacy incident onto one another.
            // ---------------------------------------------------------------
            migrationBuilder.Sql("""
                UPDATE incidents SET status = 'Detected'      WHERE status = 'Open';
                UPDATE incidents SET status = 'Investigating' WHERE status = 'Acknowledged';
                UPDATE incident_events SET type = 'InvestigationStarted' WHERE type = 'Acknowledged';
                """);

            migrationBuilder.DropIndex(
                name: "ux_incidents_active_pattern",
                table: "incidents");

            migrationBuilder.RenameIndex(
                name: "ix_log_patterns_organization_id_monitored_service_id_environme",
                table: "log_patterns",
                newName: "ix_log_patterns_organization_id_monitored_service_id_environme1");

            migrationBuilder.RenameColumn(
                name: "acknowledged_by_user_id",
                table: "incidents",
                newName: "investigating_user_id");

            migrationBuilder.RenameColumn(
                name: "acknowledged_at",
                table: "incidents",
                newName: "investigation_started_at");

            migrationBuilder.AddColumn<int>(
                name: "http_status_code",
                table: "log_patterns",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "log_pattern_id",
                table: "incidents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "dedupe_key",
                table: "incidents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "detection_rule",
                table: "incidents",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            // A legacy incident keeps its identity by taking the same dedupe key
            // the detector would compute for it, so a recurrence folds into it
            // rather than opening a parallel incident for a problem already
            // being worked on. Incidents with no pattern fall back to their own
            // id, which is unique and therefore never collides.
            migrationBuilder.Sql("""
                UPDATE incidents i
                SET dedupe_key = 'fp:' || p.fingerprint
                FROM log_patterns p
                WHERE p.id = i.log_pattern_id AND i.dedupe_key = '';

                UPDATE incidents
                SET dedupe_key = 'incident:' || id::text
                WHERE dedupe_key = '';

                UPDATE incidents SET detection_rule = 'CountThreshold' WHERE detection_rule = '';
                """);

            migrationBuilder.CreateTable(
                name: "log_pattern_metrics",
                columns: table => new
                {
                    log_pattern_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_log_pattern_metrics", x => new { x.log_pattern_id, x.bucket_start });
                    table.ForeignKey(
                        name: "fk_log_pattern_metrics_log_patterns_organization_id_log_patter",
                        columns: x => new { x.organization_id, x.log_pattern_id },
                        principalTable: "log_patterns",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_log_patterns_organization_id_monitored_service_id_environme",
                table: "log_patterns",
                columns: new[] { "organization_id", "monitored_service_id", "environment_id", "http_status_code" },
                filter: "http_status_code >= 500");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_investigating_user_id",
                table: "incidents",
                column: "investigating_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_organization_id_dedupe_key_last_seen_at",
                table: "incidents",
                columns: new[] { "organization_id", "dedupe_key", "last_seen_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_organization_id_log_pattern_id",
                table: "incidents",
                columns: new[] { "organization_id", "log_pattern_id" });

            migrationBuilder.CreateIndex(
                name: "ux_incidents_active_dedupe_key",
                table: "incidents",
                columns: new[] { "organization_id", "dedupe_key" },
                unique: true,
                filter: "status IN ('Detected', 'Investigating')");

            migrationBuilder.CreateIndex(
                name: "ix_log_pattern_metrics_bucket_start",
                table: "log_pattern_metrics",
                column: "bucket_start");

            migrationBuilder.CreateIndex(
                name: "ix_log_pattern_metrics_organization_id_log_pattern_id_bucket_s",
                table: "log_pattern_metrics",
                columns: new[] { "organization_id", "log_pattern_id", "bucket_start" },
                descending: new[] { false, false, true });

            migrationBuilder.AddForeignKey(
                name: "fk_incidents_users_investigating_user_id",
                table: "incidents",
                column: "investigating_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE incidents SET status = 'Open'         WHERE status = 'Detected';
                UPDATE incidents SET status = 'Acknowledged' WHERE status = 'Investigating';
                UPDATE incident_events SET type = 'Acknowledged' WHERE type = 'InvestigationStarted';
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_incidents_users_investigating_user_id",
                table: "incidents");

            migrationBuilder.DropTable(
                name: "log_pattern_metrics");

            migrationBuilder.DropIndex(
                name: "ix_log_patterns_organization_id_monitored_service_id_environme",
                table: "log_patterns");

            migrationBuilder.DropIndex(
                name: "ix_incidents_investigating_user_id",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "ix_incidents_organization_id_dedupe_key_last_seen_at",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "ix_incidents_organization_id_log_pattern_id",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "ux_incidents_active_dedupe_key",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "http_status_code",
                table: "log_patterns");

            migrationBuilder.DropColumn(
                name: "dedupe_key",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "detection_rule",
                table: "incidents");

            migrationBuilder.RenameIndex(
                name: "ix_log_patterns_organization_id_monitored_service_id_environme1",
                table: "log_patterns",
                newName: "ix_log_patterns_organization_id_monitored_service_id_environme");

            migrationBuilder.RenameColumn(
                name: "investigation_started_at",
                table: "incidents",
                newName: "acknowledged_at");

            migrationBuilder.RenameColumn(
                name: "investigating_user_id",
                table: "incidents",
                newName: "acknowledged_by_user_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "log_pattern_id",
                table: "incidents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_incidents_active_pattern",
                table: "incidents",
                columns: new[] { "organization_id", "log_pattern_id" },
                unique: true,
                filter: "status IN ('Open', 'Acknowledged')");
        }
    }
}
