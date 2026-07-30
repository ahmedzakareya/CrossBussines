using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossBuy.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCompaniesCompanyID : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Branches_Companies_CompaniesCompanyID",
                table: "Branches");

            migrationBuilder.DropIndex(
                name: "IX_Branches_CompaniesCompanyID",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "CompaniesCompanyID",
                table: "Branches");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_CompanyID",
                table: "Branches",
                column: "CompanyID");

            migrationBuilder.AddForeignKey(
                name: "FK_Branches_Companies_CompanyID",
                table: "Branches",
                column: "CompanyID",
                principalTable: "Companies",
                principalColumn: "CompanyID",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Branches_Companies_CompanyID",
                table: "Branches");

            migrationBuilder.DropIndex(
                name: "IX_Branches_CompanyID",
                table: "Branches");

            migrationBuilder.AddColumn<int>(
                name: "CompaniesCompanyID",
                table: "Branches",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Branches_CompaniesCompanyID",
                table: "Branches",
                column: "CompaniesCompanyID");

            migrationBuilder.AddForeignKey(
                name: "FK_Branches_Companies_CompaniesCompanyID",
                table: "Branches",
                column: "CompaniesCompanyID",
                principalTable: "Companies",
                principalColumn: "CompanyID");
        }
    }
}
