using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Photobooth.Infrastructure.Persistence.Migrations;

public partial class AddDeviceSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Chỉ tạo bảng SQLite mới. Không đọc/import app-state.json.
        migrationBuilder.CreateTable(
            name: "DeviceSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                PrinterKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                WindowsPrinterName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                WebcamDeviceId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                WebcamLabel = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                GuideMode = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                GuideOutsideOpacity = table.Column<double>(type: "REAL", nullable: false),
                GuideShowLabel = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeviceSettings", x => x.Id);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "DeviceSettings");
    }
}
