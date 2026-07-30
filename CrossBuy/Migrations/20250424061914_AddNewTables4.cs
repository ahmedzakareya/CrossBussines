using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossBuy.Migrations
{
    /// <inheritdoc />
    public partial class AddNewTables4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Hierarchicals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedBy",
                table: "Hierarchicals",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Hierarchicals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "deputyID",
                table: "Hierarchicals",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "updatedBy",
                table: "Hierarchicals",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Hierarchicals");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Hierarchicals");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Hierarchicals");

            migrationBuilder.DropColumn(
                name: "deputyID",
                table: "Hierarchicals");

            migrationBuilder.DropColumn(
                name: "updatedBy",
                table: "Hierarchicals");
        }
    }
}
