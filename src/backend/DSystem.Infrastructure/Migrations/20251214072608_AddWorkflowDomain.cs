using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Pattern = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelProviders",
                columns: table => new
                {
                    ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InputPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OutputPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CacheRead = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CacheWrite = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RpsLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelProviders", x => new { x.ModelId, x.ProviderId });
                    table.ForeignKey(
                        name: "FK_ModelProviders_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ModelProviders_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelProviderApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelProviderApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelProviderApiKeys_ModelProviders_ModelId_ProviderId",
                        columns: x => new { x.ModelId, x.ProviderId },
                        principalTable: "ModelProviders",
                        principalColumns: new[] { "ModelId", "ProviderId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    SystemPrompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ModelProviderApiKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_ModelProviderApiKeys_ModelProviderApiKeyId",
                        column: x => x.ModelProviderApiKeyId,
                        principalTable: "ModelProviderApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowAgents",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowAgents", x => new { x.WorkflowId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_WorkflowAgents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkflowAgents_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ModelProviderApiKeyId",
                table: "Agents",
                column: "ModelProviderApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelProviderApiKeys_ModelId_ProviderId",
                table: "ModelProviderApiKeys",
                columns: new[] { "ModelId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelProviders_ProviderId",
                table: "ModelProviders",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgents_AgentId",
                table: "WorkflowAgents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgents_WorkflowId_Order",
                table: "WorkflowAgents",
                columns: new[] { "WorkflowId", "Order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowAgents");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropTable(
                name: "ModelProviderApiKeys");

            migrationBuilder.DropTable(
                name: "ModelProviders");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "Providers");
        }
    }
}
