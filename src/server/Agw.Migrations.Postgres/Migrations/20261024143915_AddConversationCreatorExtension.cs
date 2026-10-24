using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationCreatorExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extension_id",
                table: "project_conversation",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "host"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "extension_id", table: "project_conversation");
        }
    }
}
