using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotaAlerting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "AlertThresholdPercentages",
                table: "tenants",
                type: "integer[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlertWebhookUrl",
                table: "tenants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlertThresholdPercentages",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "AlertWebhookUrl",
                table: "tenants");
        }
    }
}
