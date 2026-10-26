using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnUsageSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "usage_settles_at",
                table: "project_conversation_turn",
                type: "INTEGER",
                nullable: true
            );

            // 已经结束的 Turn 从结束时间起结算。列保存 DateTimeOffsetToBinaryConverter 的值：以 100 微秒为单位的 UTC 时间左移 11 位，
            // 低 11 位为与 UTC 相差的分钟数（UTC 为 0）；结束时间由 SQLite 的日期函数解析到毫秒。
            // Finished turns settle at their finish time. The column stores the DateTimeOffsetToBinaryConverter value: UTC time in 100-microsecond units shifted left by 11 bits,
            // with the offset in the low 11 bits (0 for UTC); SQLite's date functions read the finish time to the millisecond.
            migrationBuilder.Sql(
                """
                UPDATE "project_conversation_turn"
                SET "usage_settles_at" =
                    ((CAST(strftime('%s', "finished_at") AS INTEGER) + 62135596800) * 10000
                        + (CAST(ROUND(strftime('%f', "finished_at") * 1000) AS INTEGER) % 1000) * 10) * 2048
                WHERE "finished_at" IS NOT NULL;
                """
            );

            migrationBuilder.AddColumn<long>(
                name: "interrupted_lease_epoch",
                table: "durable_execution",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(name: "turn_id", table: "agent_usage", type: "TEXT", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 使用 SQLite 原生 DROP COLUMN，降级保持在同一事务内，也不会通过表重建引入外键。
            // SQLite's native DROP COLUMN keeps the downgrade in one transaction and avoids a table rebuild that would introduce a foreign key.
            migrationBuilder.Sql("ALTER TABLE \"project_conversation_turn\" DROP COLUMN \"usage_settles_at\";");
            migrationBuilder.Sql("ALTER TABLE \"durable_execution\" DROP COLUMN \"interrupted_lease_epoch\";");
            migrationBuilder.Sql("ALTER TABLE \"agent_usage\" DROP COLUMN \"turn_id\";");
        }
    }
}
