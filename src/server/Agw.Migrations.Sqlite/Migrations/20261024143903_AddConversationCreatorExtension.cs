using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
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
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "host"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 使用 SQLite 原生 DROP COLUMN，降级保持在同一事务内，也不会通过表重建引入外键。
            // SQLite's native DROP COLUMN keeps the downgrade in one transaction and avoids a table rebuild that would introduce a foreign key.
            migrationBuilder.Sql("ALTER TABLE \"project_conversation\" DROP COLUMN \"extension_id\";");
        }
    }
}
