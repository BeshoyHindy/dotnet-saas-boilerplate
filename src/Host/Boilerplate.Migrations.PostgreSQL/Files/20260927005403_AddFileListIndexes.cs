using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boilerplate.Migrations.PostgreSQL.Files
{
    /// <inheritdoc />
    public partial class AddFileListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FileAsset_CreatedBy",
                schema: "files",
                table: "FileAssets",
                columns: new[] { "CreatedByUserId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FileAsset_Shared",
                schema: "files",
                table: "FileAssets",
                columns: new[] { "TenantId", "Visibility", "Status", "OwnerType", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileAsset_CreatedBy",
                schema: "files",
                table: "FileAssets");

            migrationBuilder.DropIndex(
                name: "IX_FileAsset_Shared",
                schema: "files",
                table: "FileAssets");
        }
    }
}
