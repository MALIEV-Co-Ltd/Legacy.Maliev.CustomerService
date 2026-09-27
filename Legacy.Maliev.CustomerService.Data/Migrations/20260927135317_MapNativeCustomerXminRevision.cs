using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.CustomerService.Data.Migrations
{
    /// <inheritdoc />
    public partial class MapNativeCustomerXminRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL supplies xmin as a system column. This migration records
            // the EF model mapping without attempting to create a physical column.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous model simply did not map the existing system column.
        }
    }
}
