using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Migrations.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddApiTestSessionContextAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextAttachmentsJson",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextAttachmentsJson",
                table: "ApiTestSessions");
        }
    }
}
