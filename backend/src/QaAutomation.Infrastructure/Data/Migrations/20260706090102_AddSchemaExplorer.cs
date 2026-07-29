using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaExplorer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SchemaProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AdoProject = table.Column<string>(type: "TEXT", nullable: false),
                    AdoRepository = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaProducts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchemaConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectionString = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaConnections_SchemaProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "SchemaProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaEntities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    RootTableName = table.Column<string>(type: "TEXT", nullable: true),
                    IdColumn = table.Column<string>(type: "TEXT", nullable: true),
                    ScopeColumn = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayColumnsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaEntities_SchemaProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "SchemaProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: true),
                    CommitId = table.Column<string>(type: "TEXT", nullable: true),
                    Branch = table.Column<string>(type: "TEXT", nullable: true),
                    ServiceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TableCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RelationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EntityCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StatsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    EventLogJson = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaScans_SchemaProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "SchemaProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceTableId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetTableId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceColumn = table.Column<string>(type: "TEXT", nullable: true),
                    TargetColumn = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaRelations_SchemaScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SchemaScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DbContextClass = table.Column<string>(type: "TEXT", nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaServices_SchemaScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SchemaScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaTables",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SqlSchema = table.Column<string>(type: "TEXT", nullable: true),
                    IsKeyless = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsExternalRef = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "TEXT", nullable: true),
                    SourceFile = table.Column<string>(type: "TEXT", nullable: true),
                    ColumnsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaTables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaTables_SchemaScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SchemaScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_SchemaConnections_ProductId",
                table: "SchemaConnections",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaEntities_ProductId_Key",
                table: "SchemaEntities",
                columns: new[] { "ProductId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_SchemaRelations_ScanId",
                table: "SchemaRelations",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaRelations_SourceTableId",
                table: "SchemaRelations",
                column: "SourceTableId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaScans_ProductId_CreatedAt",
                table: "SchemaScans",
                columns: new[] { "ProductId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_SchemaServices_ScanId",
                table: "SchemaServices",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaTables_ScanId",
                table: "SchemaTables",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaTables_ServiceId",
                table: "SchemaTables",
                column: "ServiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchemaConnections");

            migrationBuilder.DropTable(
                name: "SchemaEntities");

            migrationBuilder.DropTable(
                name: "SchemaRelations");

            migrationBuilder.DropTable(
                name: "SchemaServices");

            migrationBuilder.DropTable(
                name: "SchemaTables");

            migrationBuilder.DropTable(
                name: "SchemaScans");

            migrationBuilder.DropTable(
                name: "SchemaProducts");
        }
    }
}
