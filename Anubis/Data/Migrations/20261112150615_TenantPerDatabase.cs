using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace k6labs.Anubis.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantPerDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tenants are now separate databases (the catalog lists them), and every role assignment
            // in a database holds in that database's tenant. An assignment that was limited to one
            // tenant must not turn into a role everywhere, so it goes; assignments "in all tenants" stay.
            migrationBuilder.Sql("DELETE FROM \"RoleAssignments\" WHERE \"TenantId\" IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_RoleAssignments_Tenants_TenantId",
                table: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_TenantId",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RoleAssignments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "RoleAssignments",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_TenantId",
                table: "RoleAssignments",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_RoleAssignments_Tenants_TenantId",
                table: "RoleAssignments",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
