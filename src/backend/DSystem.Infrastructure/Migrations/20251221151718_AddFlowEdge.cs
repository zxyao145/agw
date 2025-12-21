using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowEdge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowAgents");

            migrationBuilder.CreateTable(
                name: "WorkflowNodes",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    RelateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodes", x => new { x.WorkflowId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_WorkflowNodes_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkflowNodes_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowEdges",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EdgeId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceNodeId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetNodeId = table.Column<string>(type: "TEXT", nullable: false),
                    Animated = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEdges", x => new { x.WorkflowId, x.EdgeId });
                    table.ForeignKey(
                        name: "FK_WorkflowEdges_WorkflowNodes_WorkflowId_SourceNodeId",
                        columns: x => new { x.WorkflowId, x.SourceNodeId },
                        principalTable: "WorkflowNodes",
                        principalColumns: new[] { "WorkflowId", "NodeId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkflowEdges_WorkflowNodes_WorkflowId_TargetNodeId",
                        columns: x => new { x.WorkflowId, x.TargetNodeId },
                        principalTable: "WorkflowNodes",
                        principalColumns: new[] { "WorkflowId", "NodeId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkflowEdges_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEdges_WorkflowId_SourceNodeId",
                table: "WorkflowEdges",
                columns: new[] { "WorkflowId", "SourceNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEdges_WorkflowId_TargetNodeId",
                table: "WorkflowEdges",
                columns: new[] { "WorkflowId", "TargetNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodes_AgentId",
                table: "WorkflowNodes",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodes_WorkflowId_Type_RelateId",
                table: "WorkflowNodes",
                columns: new[] { "WorkflowId", "Type", "RelateId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowEdges");

            migrationBuilder.DropTable(
                name: "WorkflowNodes");

            migrationBuilder.CreateTable(
                name: "WorkflowAgents",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreateBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdateBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                name: "IX_WorkflowAgents_AgentId",
                table: "WorkflowAgents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgents_WorkflowId_Order",
                table: "WorkflowAgents",
                columns: new[] { "WorkflowId", "Order" },
                unique: true);
        }
    }
}
