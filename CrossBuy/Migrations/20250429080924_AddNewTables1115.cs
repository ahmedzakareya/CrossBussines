using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossBuy.Migrations
{
    /// <inheritdoc />
    public partial class AddNewTables1115 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePolicies_LeaveTypes_LeavePolicyTypeID",
                table: "AttendancePolicies");

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
                name: "FK_AttendancePolicies_Policies_LeavePolicyTypeID",
                table: "AttendancePolicies",
                column: "LeavePolicyTypeID",
                principalTable: "Policies",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePolicies_LeaveTypes_LeaveTypesID",
                table: "AttendancePolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePolicies_Policies_LeavePolicyTypeID",
                table: "AttendancePolicies");

            migrationBuilder.DropIndex(
                name: "IX_AttendancePolicies_LeaveTypesID",
                table: "AttendancePolicies");

            migrationBuilder.DropColumn(
                name: "LeaveTypesID",
                table: "AttendancePolicies");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendancePolicies_LeaveTypes_LeavePolicyTypeID",
                table: "AttendancePolicies",
                column: "LeavePolicyTypeID",
                principalTable: "LeaveTypes",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
