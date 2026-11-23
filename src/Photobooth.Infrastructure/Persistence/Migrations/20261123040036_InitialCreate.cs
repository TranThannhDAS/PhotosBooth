using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Photobooth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, collation: "NOCASE"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastOpenedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectoryPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, collation: "NOCASE"),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TemplateId = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateName = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateFile = table.Column<string>(type: "TEXT", nullable: true),
                    Mirror = table.Column<bool>(type: "INTEGER", nullable: false),
                    Filter = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SelectedPhotoIds = table.Column<string>(type: "TEXT", nullable: false),
                    SlotCrops = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Exports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ExportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Exports_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutputVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ThumbnailPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    WidthPixels = table.Column<int>(type: "INTEGER", nullable: false),
                    HeightPixels = table.Column<int>(type: "INTEGER", nullable: false),
                    Dpi = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Filter = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PhotoIds = table.Column<string>(type: "TEXT", nullable: false),
                    SlotCrops = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ShareToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ShareUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ShareCreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutputVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutputVersions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Photos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    WidthPixels = table.Column<int>(type: "INTEGER", nullable: false),
                    HeightPixels = table.Column<int>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Mirrored = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Photos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Photos_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Copies = table.Column<int>(type: "INTEGER", nullable: false),
                    PrinterName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    IsPhysicalPrinter = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintJobs_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CloudUploads",
                columns: table => new
                {
                    OutputVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UploadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudUploads", x => x.OutputVersionId);
                    table.ForeignKey(
                        name: "FK_CloudUploads_OutputVersions_OutputVersionId",
                        column: x => x.OutputVersionId,
                        principalTable: "OutputVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloudUploads_Status",
                table: "CloudUploads",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Events_FolderPath",
                table: "Events",
                column: "FolderPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exports_SessionId",
                table: "Exports",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputVersions_SessionId_Version",
                table: "OutputVersions",
                columns: new[] { "SessionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutputVersions_ShareToken",
                table: "OutputVersions",
                column: "ShareToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Photos_SessionId_Sequence",
                table: "Photos",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_SessionId",
                table: "PrintJobs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_DirectoryPath",
                table: "Sessions",
                columns: new[] { "EventId", "DirectoryPath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudUploads");

            migrationBuilder.DropTable(
                name: "Exports");

            migrationBuilder.DropTable(
                name: "Photos");

            migrationBuilder.DropTable(
                name: "PrintJobs");

            migrationBuilder.DropTable(
                name: "OutputVersions");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
