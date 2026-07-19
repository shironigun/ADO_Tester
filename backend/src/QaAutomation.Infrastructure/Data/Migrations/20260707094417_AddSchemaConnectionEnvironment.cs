using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaConnectionEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Environment",
                table: "SchemaConnections",
                type: "TEXT",
                nullable: false,
                defaultValue: "Default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Environment",
                table: "SchemaConnections");
        }
    }
}
