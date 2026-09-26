using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.CustomerService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCreateReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerCreateOperation",
                columns: table => new
                {
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerCreateOperation", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Customer-create replay history cannot be discarded by a schema rollback; use a reviewed forward-fix migration.");
        }
    }
}
