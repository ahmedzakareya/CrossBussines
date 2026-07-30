using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossBuy.Migrations
{
    /// <inheritdoc />
    public partial class AddNewTables11 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttendancePolicies",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeavePolicyTypeID = table.Column<int>(type: "int", nullable: false),
                    AllowedGraceMinutes = table.Column<int>(type: "int", nullable: false),
                    WarningThresholdCount = table.Column<int>(type: "int", nullable: false),
                    DeductionRatePerOccurrence = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UnexcusedAbsenceThreshold = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendancePolicies", x => x.ID);
                    table.ForeignKey(
                        name: "FK_AttendancePolicies_LeaveTypes_LeavePolicyTypeID",
                        column: x => x.LeavePolicyTypeID,
                        principalTable: "LeaveTypes",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PolicyAssignments",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeavePolicyTypeID = table.Column<int>(type: "int", nullable: false),
                    EmployeeID = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyAssignments", x => x.ID);
                    table.ForeignKey(
                        name: "FK_PolicyAssignments_Employee_EmployeeID",
                        column: x => x.EmployeeID,
                        principalTable: "Employee",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyAssignments_LeaveTypes_LeavePolicyTypeID",
                        column: x => x.LeavePolicyTypeID,
                        principalTable: "LeaveTypes",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePolicies_LeavePolicyTypeID",
                table: "AttendancePolicies",
                column: "LeavePolicyTypeID");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAssignments_EmployeeID",
                table: "PolicyAssignments",
                column: "EmployeeID");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAssignments_LeavePolicyTypeID",
                table: "PolicyAssignments",
                column: "LeavePolicyTypeID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendancePolicies");

            migrationBuilder.DropTable(
                name: "PolicyAssignments");
        }
    }
}
