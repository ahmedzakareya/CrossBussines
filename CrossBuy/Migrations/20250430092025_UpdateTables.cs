using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossBuy.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePolicies_LeaveTypes_LeaveTypesID",
                table: "AttendancePolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_LeavePolicies_LeaveTypes_LeavePolicyTypeID",
                table: "LeavePolicies");

            migrationBuilder.DropIndex(
                name: "IX_AttendancePolicies_LeaveTypesID",
                table: "AttendancePolicies");

            migrationBuilder.DropColumn(
                name: "LeaveTypesID",
                table: "AttendancePolicies");

            migrationBuilder.AddColumn<int>(
                name: "LeaveTypeID",
                table: "LeavePolicies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SalaryPoliciesID",
                table: "Employee",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalaryPolicies",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeavePolicyTypeID = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BaseSalary = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    HousingAllowance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TransportationAllowance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OtherAllowances = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OvertimeRate = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LatePenaltyPerMinute = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AbsencePenaltyPerDay = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SocialInsuranceEmployeeShare = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SocialInsuranceCompanyShare = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxRate = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsTaxApplicable = table.Column<bool>(type: "bit", nullable: false),
                    PaymentDay = table.Column<int>(type: "int", nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryPolicies", x => x.ID);
                    table.ForeignKey(
                        name: "FK_SalaryPolicies_Policies_LeavePolicyTypeID",
                        column: x => x.LeavePolicyTypeID,
                        principalTable: "Policies",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeavePolicies_LeaveTypeID",
                table: "LeavePolicies",
                column: "LeaveTypeID");

            migrationBuilder.CreateIndex(
                name: "IX_Employee_SalaryPoliciesID",
                table: "Employee",
                column: "SalaryPoliciesID");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryPolicies_LeavePolicyTypeID",
                table: "SalaryPolicies",
                column: "LeavePolicyTypeID");

            migrationBuilder.AddForeignKey(
                name: "FK_Employee_SalaryPolicies_SalaryPoliciesID",
                table: "Employee",
                column: "SalaryPoliciesID",
                principalTable: "SalaryPolicies",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_LeavePolicies_LeaveTypes_LeaveTypeID",
                table: "LeavePolicies",
                column: "LeaveTypeID",
                principalTable: "LeaveTypes",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LeavePolicies_Policies_LeavePolicyTypeID",
                table: "LeavePolicies",
                column: "LeavePolicyTypeID",
                principalTable: "Policies",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Employee_SalaryPolicies_SalaryPoliciesID",
                table: "Employee");

            migrationBuilder.DropForeignKey(
                name: "FK_LeavePolicies_LeaveTypes_LeaveTypeID",
                table: "LeavePolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_LeavePolicies_Policies_LeavePolicyTypeID",
                table: "LeavePolicies");

            migrationBuilder.DropTable(
                name: "SalaryPolicies");

            migrationBuilder.DropIndex(
                name: "IX_LeavePolicies_LeaveTypeID",
                table: "LeavePolicies");

            migrationBuilder.DropIndex(
                name: "IX_Employee_SalaryPoliciesID",
                table: "Employee");

            migrationBuilder.DropColumn(
                name: "LeaveTypeID",
                table: "LeavePolicies");

            migrationBuilder.DropColumn(
                name: "SalaryPoliciesID",
                table: "Employee");

            migrationBuilder.AddColumn<int>(
                name: "LeaveTypesID",
                table: "AttendancePolicies",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePolicies_LeaveTypesID",
                table: "AttendancePolicies",
                column: "LeaveTypesID");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendancePolicies_LeaveTypes_LeaveTypesID",
                table: "AttendancePolicies",
                column: "LeaveTypesID",
                principalTable: "LeaveTypes",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_LeavePolicies_LeaveTypes_LeavePolicyTypeID",
                table: "LeavePolicies",
                column: "LeavePolicyTypeID",
                principalTable: "LeaveTypes",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
