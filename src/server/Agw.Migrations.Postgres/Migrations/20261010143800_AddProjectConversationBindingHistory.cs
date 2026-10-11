using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectConversationBindingHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_conversation_binding_project_conversation_id_agent_",
                table: "project_conversation_binding"
            );

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "project_conversation_binding",
                type: "boolean",
                nullable: false,
                defaultValue: true
            );

            migrationBuilder.CreateIndex(
                name: "ux_project_conversation_binding_active",
                table: "project_conversation_binding",
                columns: new[] { "project_conversation_id", "agent_id", "external_agent_name" },
                unique: true,
                filter: "is_active = TRUE"
            );

            migrationBuilder.CreateIndex(
                name: "ux_project_conversation_binding_session",
                table: "project_conversation_binding",
                columns: new[] { "project_conversation_id", "agent_id", "external_agent_name", "provider_session_id" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 先恢复原唯一索引：同一绑定组已有多条 session 记录时降级在这里终止，新索引、字段与全部记录保持不变。
            // Restore the original unique index first: when a binding group already has several session records the downgrade stops here, leaving the new indexes, column and every record unchanged.
            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_binding_project_conversation_id_agent_",
                table: "project_conversation_binding",
                columns: new[] { "project_conversation_id", "agent_id", "external_agent_name" },
                unique: true
            );

            migrationBuilder.DropIndex(
                name: "ux_project_conversation_binding_active",
                table: "project_conversation_binding"
            );

            migrationBuilder.DropIndex(
                name: "ux_project_conversation_binding_session",
                table: "project_conversation_binding"
            );

            migrationBuilder.DropColumn(name: "is_active", table: "project_conversation_binding");
        }
    }
}
