using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.CustomerService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationProfileCompletionReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuotationProfileCompletionOperation",
                columns: table => new
                {
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CompletionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Changed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationProfileCompletionOperation", x => new { x.CustomerId, x.Key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Durable profile-completion receipts require a forward-fix migration; rollback must not erase replay history.");
        }
    }
}
