using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectRefactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_providers_name",
                table: "providers",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_name",
                table: "projects",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_models_name",
                table: "models",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_providers_name",
                table: "providers");

            migrationBuilder.DropIndex(
                name: "ix_projects_name",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_models_name",
                table: "models");

            migrationBuilder.DropColumn(
                name: "type",
                table: "projects");
        }
    }
}
