using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Photobooth.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "photos",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    share_token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    storage_path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_photos", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_photos_share_token",
                table: "photos",
                column: "share_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_photos_storage_path",
                table: "photos",
                column: "storage_path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photos");
        }
    }
}
