using Agw.Extensions.Sdk.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Extensions.TaskPilot.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class TaskPilotSqliteResetEventSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Operations.Add(
                new ExtensionUpdateAllRowsOperation
                {
                    Table = "zext_019a4c175f007a808b3d2b732f48a101_user_state",
                    Assignments =
                    [
                        new("system_subscription_id", null),
                        new("system_subscription_outbox_id", null),
                        new("system_subscription_error", null),
                    ],
                }
            );
            migrationBuilder.Operations.Add(
                new ExtensionUpdateAllRowsOperation
                {
                    Table = "zext_019a4c175f007a808b3d2b732f48a101_rule",
                    Assignments =
                    [
                        new("subscription_id", null),
                        new("subscription_outbox_id", null),
                        new("subscription_error", "Please save the rule again."),
                    ],
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
