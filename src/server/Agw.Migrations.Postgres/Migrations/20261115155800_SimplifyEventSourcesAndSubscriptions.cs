using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyEventSourcesAndSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM event_delivery_attempt;");
            migrationBuilder.Sql("DELETE FROM event_delivery;");
            migrationBuilder.Sql("DELETE FROM event_outbox;");
            migrationBuilder.Sql("DELETE FROM event;");
            migrationBuilder.Sql("DELETE FROM event_schedule;");
            migrationBuilder.Sql("DELETE FROM event_source_item;");
            migrationBuilder.Sql("DELETE FROM event_subscription;");
            migrationBuilder.Sql("DELETE FROM event_source;");

            migrationBuilder.DropIndex(name: "ix_event_source_create_by_publisher_key", table: "event_source");

            migrationBuilder.DropColumn(name: "publisher_keys_json", table: "event_subscription");

            migrationBuilder.DropColumn(name: "publisher_key", table: "event_source");

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "event_source",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text"
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_discarded_at",
                table: "event_source",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "last_discarded_type",
                table: "event_source",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_source_create_by_name",
                table: "event_source",
                columns: new[] { "create_by", "name" },
                unique: true,
                filter: "deleted_at IS NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_event_source_create_by_name", table: "event_source");

            migrationBuilder.DropColumn(name: "last_discarded_at", table: "event_source");

            migrationBuilder.DropColumn(name: "last_discarded_type", table: "event_source");

            migrationBuilder.AddColumn<string>(
                name: "publisher_keys_json",
                table: "event_subscription",
                type: "text",
                nullable: true
            );

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "event_source",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200
            );

            migrationBuilder.AddColumn<string>(
                name: "publisher_key",
                table: "event_source",
                type: "text",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_source_create_by_publisher_key",
                table: "event_source",
                columns: new[] { "create_by", "publisher_key" },
                unique: true,
                filter: "publisher_key IS NOT NULL"
            );
        }
    }
}
