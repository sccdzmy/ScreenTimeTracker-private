using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Migrations;

[DbContext(typeof(ScreenTimeDbContext))]
[Migration("20261010000000_SessionIdentityStartIndexes")]
public sealed partial class SessionIdentityStartIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex("IX_ScreenTime_AppUsageSessions_AppId_StartTime",
            "ScreenTime_AppUsageSessions", new[] { "AppId", "StartTime" });
        migrationBuilder.CreateIndex("IX_ScreenTime_WebsiteUsageSessions_WebsiteId_StartTime",
            "ScreenTime_WebsiteUsageSessions", new[] { "WebsiteId", "StartTime" });
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_ScreenTime_AppUsageSessions_AppId_StartTime", "ScreenTime_AppUsageSessions");
        migrationBuilder.DropIndex("IX_ScreenTime_WebsiteUsageSessions_WebsiteId_StartTime", "ScreenTime_WebsiteUsageSessions");
    }
}
