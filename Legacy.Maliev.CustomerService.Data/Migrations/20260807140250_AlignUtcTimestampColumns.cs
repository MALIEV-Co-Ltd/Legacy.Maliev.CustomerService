using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.CustomerService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlignUtcTimestampColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve each existing instant as UTC while moving from timestamptz to
            // the legacy UTC wall-clock representation used by the migrated database.
            migrationBuilder.Sql(
                """
                ALTER TABLE "Customer" ALTER COLUMN "ModifiedDate" DROP DEFAULT;
                ALTER TABLE "Customer" ALTER COLUMN "ModifiedDate" TYPE timestamp without time zone USING "ModifiedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Customer" ALTER COLUMN "ModifiedDate" SET DEFAULT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
                ALTER TABLE "Customer" ALTER COLUMN "CreatedDate" DROP DEFAULT;
                ALTER TABLE "Customer" ALTER COLUMN "CreatedDate" TYPE timestamp without time zone USING "CreatedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Customer" ALTER COLUMN "CreatedDate" SET DEFAULT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
                ALTER TABLE "Company" ALTER COLUMN "ModifiedDate" DROP DEFAULT;
                ALTER TABLE "Company" ALTER COLUMN "ModifiedDate" TYPE timestamp without time zone USING "ModifiedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Company" ALTER COLUMN "ModifiedDate" SET DEFAULT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
                ALTER TABLE "Company" ALTER COLUMN "CreatedDate" DROP DEFAULT;
                ALTER TABLE "Company" ALTER COLUMN "CreatedDate" TYPE timestamp without time zone USING "CreatedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Company" ALTER COLUMN "CreatedDate" SET DEFAULT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
                ALTER TABLE "Address" ALTER COLUMN "ModifiedDate" DROP DEFAULT;
                ALTER TABLE "Address" ALTER COLUMN "ModifiedDate" TYPE timestamp without time zone USING "ModifiedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Address" ALTER COLUMN "ModifiedDate" SET DEFAULT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
                ALTER TABLE "Address" ALTER COLUMN "CreatedDate" DROP DEFAULT;
                ALTER TABLE "Address" ALTER COLUMN "CreatedDate" TYPE timestamp without time zone USING "CreatedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Address" ALTER COLUMN "CreatedDate" SET DEFAULT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Customer" ALTER COLUMN "ModifiedDate" DROP DEFAULT;
                ALTER TABLE "Customer" ALTER COLUMN "ModifiedDate" TYPE timestamp with time zone USING "ModifiedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Customer" ALTER COLUMN "ModifiedDate" SET DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE "Customer" ALTER COLUMN "CreatedDate" DROP DEFAULT;
                ALTER TABLE "Customer" ALTER COLUMN "CreatedDate" TYPE timestamp with time zone USING "CreatedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Customer" ALTER COLUMN "CreatedDate" SET DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE "Company" ALTER COLUMN "ModifiedDate" DROP DEFAULT;
                ALTER TABLE "Company" ALTER COLUMN "ModifiedDate" TYPE timestamp with time zone USING "ModifiedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Company" ALTER COLUMN "ModifiedDate" SET DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE "Company" ALTER COLUMN "CreatedDate" DROP DEFAULT;
                ALTER TABLE "Company" ALTER COLUMN "CreatedDate" TYPE timestamp with time zone USING "CreatedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Company" ALTER COLUMN "CreatedDate" SET DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE "Address" ALTER COLUMN "ModifiedDate" DROP DEFAULT;
                ALTER TABLE "Address" ALTER COLUMN "ModifiedDate" TYPE timestamp with time zone USING "ModifiedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Address" ALTER COLUMN "ModifiedDate" SET DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE "Address" ALTER COLUMN "CreatedDate" DROP DEFAULT;
                ALTER TABLE "Address" ALTER COLUMN "CreatedDate" TYPE timestamp with time zone USING "CreatedDate" AT TIME ZONE 'UTC';
                ALTER TABLE "Address" ALTER COLUMN "CreatedDate" SET DEFAULT CURRENT_TIMESTAMP;
                """);
        }
    }
}
