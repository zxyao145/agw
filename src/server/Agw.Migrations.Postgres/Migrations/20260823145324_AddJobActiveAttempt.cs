using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddJobActiveAttempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "active_attempt_started_at",
                table: "job",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(name: "active_execution_id", table: "job", type: "uuid", nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_active_execution_id",
                table: "job",
                column: "active_execution_id",
                unique: true
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_active_attempt",
                table: "job",
                sql: "(status = 2 AND active_execution_id IS NOT NULL AND active_attempt_started_at IS NOT NULL) OR (status <> 2 AND active_execution_id IS NULL AND active_attempt_started_at IS NULL)"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_job_active_execution_id", table: "job");

            migrationBuilder.DropCheckConstraint(name: "ck_job_active_attempt", table: "job");

            migrationBuilder.DropColumn(name: "active_attempt_started_at", table: "job");

            migrationBuilder.DropColumn(name: "active_execution_id", table: "job");
        }
    }
}
