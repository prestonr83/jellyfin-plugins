using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Postgres.Data.Migrations
{
    /// <inheritdoc />
    public partial class Jellyfin124 : Migration
    {
        private static readonly string[] ExtraTypeOwnerColumns = { "ExtraType", "OwnerId" };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: ExtraTypeOwnerColumns,
                filter: "\"ExtraType\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId",
                filter: "\"OwnerId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: ExtraTypeOwnerColumns);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId");
        }
    }
}
