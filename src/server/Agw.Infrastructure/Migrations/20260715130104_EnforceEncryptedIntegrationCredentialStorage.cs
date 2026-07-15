using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceEncryptedIntegrationCredentialStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM plugin_installation_credential " +
                "WHERE protected_value IS NULL OR protected_value = '';");
            migrationBuilder.Sql(
                "DELETE FROM integration_connection_credential " +
                "WHERE protected_value IS NULL OR protected_value = '';");

            migrationBuilder.DropColumn(
                name: "display_hint",
                table: "plugin_installation_credential");

            migrationBuilder.DropColumn(
                name: "environment_variable_name",
                table: "plugin_installation_credential");

            migrationBuilder.DropColumn(
                name: "source",
                table: "plugin_installation_credential");

            migrationBuilder.DropColumn(
                name: "display_hint",
                table: "integration_connection_credential");

            migrationBuilder.DropColumn(
                name: "environment_variable_name",
                table: "integration_connection_credential");

            migrationBuilder.DropColumn(
                name: "source",
                table: "integration_connection_credential");

            migrationBuilder.AlterColumn<string>(
                name: "protected_value",
                table: "plugin_installation_credential",
                type: "TEXT",
                maxLength: 16000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "protected_value",
                table: "integration_connection_credential",
                type: "TEXT",
                maxLength: 16000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "protected_value",
                table: "plugin_installation_credential",
                type: "TEXT",
                maxLength: 16000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16000);

            migrationBuilder.AddColumn<string>(
                name: "display_hint",
                table: "plugin_installation_credential",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "environment_variable_name",
                table: "plugin_installation_credential",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "plugin_installation_credential",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "EncryptedValue");

            migrationBuilder.AlterColumn<string>(
                name: "protected_value",
                table: "integration_connection_credential",
                type: "TEXT",
                maxLength: 16000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16000);

            migrationBuilder.AddColumn<string>(
                name: "display_hint",
                table: "integration_connection_credential",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "environment_variable_name",
                table: "integration_connection_credential",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "integration_connection_credential",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "EncryptedValue");
        }
    }
}
