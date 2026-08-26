using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentIQ.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendOutboxForPublisher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages");

            migrationBuilder.AddColumn<Guid>(
                name: "correlation_id",
                table: "outbox_messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dead_lettered_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "event_id",
                table: "outbox_messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "event_version",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "occurred_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "partition_key",
                table: "outbox_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "topic",
                table: "outbox_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_correlation_id",
                table: "outbox_messages",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dead_lettered_at",
                table: "outbox_messages",
                column: "dead_lettered_at",
                filter: "dead_lettered_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_event_id",
                table: "outbox_messages",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                columns: new[] { "next_attempt_at", "id" },
                filter: "published_at IS NULL AND dead_lettered_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_correlation_id",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_dead_lettered_at",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_event_id",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "dead_lettered_at",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "event_id",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "event_version",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "occurred_at",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "partition_key",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "topic",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");
        }
    }
}
