using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace k6labs.Anubis.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantsAndRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientRoles_Applications_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.CheckConstraint("CK_RoleAssignments_UserOrGroup", "(\"UserId\" IS NULL) <> (\"GroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_RoleAssignments_ClientRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "ClientRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientRoles_ClientId_Name",
                table: "ClientRoles",
                columns: new[] { "ClientId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_GroupId",
                table: "RoleAssignments",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_TenantId",
                table: "RoleAssignments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_UserId",
                table: "RoleAssignments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "ClientRoles");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
