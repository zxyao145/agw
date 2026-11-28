using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionStreamEntryMessageType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "message_type",
                table: "execution_stream_entry",
                type: "text",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_execution_stream_entry_turn_id_message_type_turn_sequence",
                table: "execution_stream_entry",
                columns: new[] { "turn_id", "message_type", "turn_sequence" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_execution_stream_entry_turn_id_message_type_turn_sequence",
                table: "execution_stream_entry"
            );

            migrationBuilder.DropColumn(name: "message_type", table: "execution_stream_entry");
        }
    }
}
