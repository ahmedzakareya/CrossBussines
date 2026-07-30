using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossBuy.Migrations
{
    /// <inheritdoc />
    public partial class AddNewTables114 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "LeaveTypes",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Notes",
                table: "LeaveTypes");
        }
    }
}
