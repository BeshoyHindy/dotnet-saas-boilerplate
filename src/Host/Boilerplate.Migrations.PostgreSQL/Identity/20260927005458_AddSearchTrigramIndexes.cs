using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boilerplate.Migrations.PostgreSQL.Identity
{
    /// <inheritdoc />
    public partial class AddSearchTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email_trgm",
                schema: "identity",
                table: "Users",
                column: "Email")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_FirstName_trgm",
                schema: "identity",
                table: "Users",
                column: "FirstName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_LastName_trgm",
                schema: "identity",
                table: "Users",
                column: "LastName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName_trgm",
                schema: "identity",
                table: "Users",
                column: "UserName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Description_trgm",
                schema: "identity",
                table: "Groups",
                column: "Description")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Name_trgm",
                schema: "identity",
                table: "Groups",
                column: "Name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email_trgm",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_FirstName_trgm",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_LastName_trgm",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_UserName_trgm",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Groups_Description_trgm",
                schema: "identity",
                table: "Groups");

            migrationBuilder.DropIndex(
                name: "IX_Groups_Name_trgm",
                schema: "identity",
                table: "Groups");

            // pg_trgm is deliberately left installed: the Audit context's trigram indexes depend on
            // it too, so DROP EXTENSION would fail (or, with CASCADE, take them with it).
        }
    }
}
