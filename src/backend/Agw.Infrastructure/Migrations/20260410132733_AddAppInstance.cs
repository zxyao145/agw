using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oauth_authorization_provider",
                table: "oauth_authorization");

            migrationBuilder.DropIndex(
                name: "ix_oauth_authorization_provider_subject",
                table: "oauth_authorization");

            migrationBuilder.DropColumn(
                name: "create_by",
                table: "oauth_authorization");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "oauth_authorization");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "oauth_authorization");

            migrationBuilder.DropColumn(
                name: "update_by",
                table: "oauth_authorization");

            migrationBuilder.DropColumn(
                name: "update_time",
                table: "oauth_authorization");

            migrationBuilder.RenameColumn(
                name: "create_time",
                table: "oauth_authorization",
                newName: "app_instance_id");

            migrationBuilder.CreateTable(
                name: "app_instance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    app_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    use_pkce = table.Column<bool>(type: "INTEGER", nullable: false),
                    client_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    client_secret = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_instance", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_app_instance_id",
                table: "oauth_authorization",
                column: "app_instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_instance_app_name",
                table: "app_instance",
                column: "app_name");

            migrationBuilder.CreateIndex(
                name: "ix_app_instance_client_id",
                table: "app_instance",
                column: "client_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_instance");

            migrationBuilder.DropIndex(
                name: "ix_oauth_authorization_app_instance_id",
                table: "oauth_authorization");

            migrationBuilder.RenameColumn(
                name: "app_instance_id",
                table: "oauth_authorization",
                newName: "create_time");

            migrationBuilder.AddColumn<string>(
                name: "create_by",
                table: "oauth_authorization",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "oauth_authorization",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "oauth_authorization",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_by",
                table: "oauth_authorization",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "update_time",
                table: "oauth_authorization",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_provider",
                table: "oauth_authorization",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_provider_subject",
                table: "oauth_authorization",
                columns: new[] { "provider", "subject" });
        }
    }
}
