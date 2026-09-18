using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boilerplate.Migrations.PostgreSQL.MultiTenancy
{
    /// <inheritdoc />
    public partial class InitialMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenant");

            migrationBuilder.CreateTable(
                name: "TenantExpiryNotices",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NoticeType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValidUptoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantExpiryNotices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantProvisionings",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentStep = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    JobId = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantProvisionings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ConnectionString = table.Column<string>(type: "text", nullable: false),
                    AdminEmail = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ValidUpto = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Issuer = table.Column<string>(type: "text", nullable: true),
                    Plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    QuotaLimits = table.Column<string>(type: "jsonb", nullable: false),
                    Identifier = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantThemes",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrimaryColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    SecondaryColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    TertiaryColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    BackgroundColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    SurfaceColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    ErrorColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    WarningColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    SuccessColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    InfoColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkPrimaryColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkSecondaryColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkTertiaryColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkBackgroundColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkSurfaceColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkErrorColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkWarningColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkSuccessColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DarkInfoColor = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    LogoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LogoDarkUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FaviconUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FontFamily = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HeadingFontFamily = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FontSizeBase = table.Column<double>(type: "double precision", nullable: false),
                    LineHeightBase = table.Column<double>(type: "double precision", nullable: false),
                    BorderRadius = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultElevation = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastModifiedOnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastModifiedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantThemes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantProvisioningSteps",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProvisioningId = table.Column<Guid>(type: "uuid", nullable: false),
                    Step = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantProvisioningSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantProvisioningSteps_TenantProvisionings_ProvisioningId",
                        column: x => x.ProvisioningId,
                        principalSchema: "tenant",
                        principalTable: "TenantProvisionings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_expiry_notices_tenant_type_validupto",
                schema: "tenant",
                table: "TenantExpiryNotices",
                columns: new[] { "TenantId", "NoticeType", "ValidUptoUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantProvisioningSteps_ProvisioningId",
                schema: "tenant",
                table: "TenantProvisioningSteps",
                column: "ProvisioningId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Identifier",
                schema: "tenant",
                table: "Tenants",
                column: "Identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantThemes_TenantId",
                schema: "tenant",
                table: "TenantThemes",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantExpiryNotices",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "TenantProvisioningSteps",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "TenantThemes",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "TenantProvisionings",
                schema: "tenant");
        }
    }
}
