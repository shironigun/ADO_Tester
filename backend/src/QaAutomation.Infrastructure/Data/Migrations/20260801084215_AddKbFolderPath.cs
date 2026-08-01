using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKbFolderPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FolderPath",
                table: "Documents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderPath",
                table: "Documents");
        }
    }
}
