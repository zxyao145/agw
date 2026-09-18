using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOidcLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_desktop_login_grant",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_challenge = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                    session_version = table.Column<int>(type: "INTEGER", nullable: false),
                    expires_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_desktop_login_grant", x => x.code_hash);
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_external_identity",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    issuer = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_external_identity", x => x.user_id);
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_user",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    session_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_user", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_user_id_sequence",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    next_id = table.Column<long>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_user_id_sequence", x => x.id);
                    table.CheckConstraint("ck_auth_user_id_sequence_singleton", "id = 1");
                    table.CheckConstraint("ck_auth_user_id_sequence_start", "next_id >= 10000");
                }
            );

            migrationBuilder.InsertData(
                table: "auth_user",
                columns: new[]
                {
                    "id",
                    "create_by",
                    "create_time",
                    "display_name",
                    "email",
                    "session_version",
                    "update_by",
                    "update_time",
                },
                values: new object[]
                {
                    1001L,
                    "1001",
                    new DateTimeOffset(
                        new DateTime(2026, 9, 17, 0, 0, 0, 0, DateTimeKind.Unspecified),
                        new TimeSpan(0, 0, 0, 0, 0)
                    ),
                    "admin",
                    null,
                    1,
                    null,
                    null,
                }
            );

            migrationBuilder.InsertData(
                table: "auth_user_id_sequence",
                columns: new[] { "id", "next_id" },
                values: new object[] { 1, 10000L }
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_desktop_login_grant_expires_at_ms",
                table: "auth_desktop_login_grant",
                column: "expires_at_ms"
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_external_identity_issuer_subject",
                table: "auth_external_identity",
                columns: new[] { "issuer", "subject" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "auth_desktop_login_grant");

            migrationBuilder.DropTable(name: "auth_external_identity");

            migrationBuilder.DropTable(name: "auth_user");

            migrationBuilder.DropTable(name: "auth_user_id_sequence");
        }
    }
}
