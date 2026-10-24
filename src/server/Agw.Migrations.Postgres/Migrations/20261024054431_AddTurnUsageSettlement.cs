using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnUsageSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "usage_settles_at",
                table: "project_conversation_turn",
                type: "timestamp with time zone",
                nullable: true
            );

            // 已经结束的 Turn 从结束时间起结算。
            // Finished turns settle at their finish time.
            migrationBuilder.Sql(
                """
                UPDATE "project_conversation_turn"
                SET "usage_settles_at" = "finished_at"
                WHERE "finished_at" IS NOT NULL;
                """
            );

            migrationBuilder.AddColumn<long>(
                name: "interrupted_lease_epoch",
                table: "durable_execution",
                type: "bigint",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(name: "turn_id", table: "agent_usage", type: "uuid", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "usage_settles_at", table: "project_conversation_turn");

            migrationBuilder.DropColumn(name: "interrupted_lease_epoch", table: "durable_execution");

            migrationBuilder.DropColumn(name: "turn_id", table: "agent_usage");
        }
    }
}
