using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Migrations.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeBehaviorJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeBehaviorJson",
                table: "TestSessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeBehaviorJson",
                table: "TestSessions");
        }
    }
}
