using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossBuy.Migrations
{
    /// <inheritdoc />
    public partial class AddNewTables112 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PolicyAssignments_LeaveTypes_LeavePolicyTypeID",
                table: "PolicyAssignments");

            migrationBuilder.CreateTable(
                name: "Policies",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.ID);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_PolicyAssignments_Policies_LeavePolicyTypeID",
                table: "PolicyAssignments",
                column: "LeavePolicyTypeID",
                principalTable: "Policies",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PolicyAssignments_Policies_LeavePolicyTypeID",
                table: "PolicyAssignments");

            migrationBuilder.DropTable(
                name: "Policies");

            migrationBuilder.AddForeignKey(
                name: "FK_PolicyAssignments_LeaveTypes_LeavePolicyTypeID",
                table: "PolicyAssignments",
                column: "LeavePolicyTypeID",
                principalTable: "LeaveTypes",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
