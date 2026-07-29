using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaConnectionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DbDatabase",
                table: "SchemaConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DbUser",
                table: "SchemaConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Server",
                table: "SchemaConnections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DbDatabase",
                table: "SchemaConnections");

            migrationBuilder.DropColumn(
                name: "DbUser",
                table: "SchemaConnections");

            migrationBuilder.DropColumn(
                name: "Server",
                table: "SchemaConnections");
        }
    }
}
