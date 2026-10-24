using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Domain;
using Legacy.Maliev.CustomerService.Tests.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.CustomerService.Tests.Data;

/// <summary>Verifies the Company-only migration in uniquely named, disposable databases.</summary>
/// <param name="fixture">The existing fixture owning the disposable PostgreSQL server.</param>
[Collection("Customer company lifecycle")]
public sealed class CompanySourceStorageMigrationTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string Previous = "20260930050259_AddQuotationProfileCompletionReplay";
    private const string Target = "20261024150000_EnforceCompanySourceStorage";

    /// <summary>Checks runtime, snapshot, and migration target models with no pending schema changes.</summary>
    /// <returns>The model and physical migration assertions.</returns>
    [Fact]
    public async Task ModelAndSnapshotRetainThreeCapacityChecksAndNullableMetadata()
    {
        await RunAsync(async (db, token) =>
        {
            Assert.False(db.Database.HasPendingModelChanges());
            var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
            Assert.NotNull(snapshot);
            foreach (var model in new[] { db.GetService<IDesignTimeModel>().Model, snapshot.Model })
            {
                var company = model.FindEntityType(typeof(Company).FullName!);
                Assert.NotNull(company);
                foreach (var field in new[] { "Name", "TaxNumber", "Registrar" })
                {
                    var property = company.FindProperty(field);
                    Assert.NotNull(property);
                    Assert.Equal(field != "Name", property.IsNullable);
                    Assert.Equal(256, property.GetMaxLength());
                    Assert.Equal("text", property.GetColumnType());
                    Assert.Contains(company.GetCheckConstraints(), value => value.Name == $"CK_Company_{field}_SourceLength");
                }
            }
            Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync(token));
        });
    }

    /// <summary>Verifies reversible values and schema, explicit Company-only rollback row-version changes, and restored session settings.</summary>
    /// <returns>The exact row/schema/session checks around up, down, and repeated up.</returns>
    [Fact]
    public async Task UpDownUpPreservesRowsRelationsDefaultsAndSessionTimeouts()
    {
        await RunAsync(async (db, token) =>
        {
            await SeedAsync(db, token);
            await SetAsync(db, "Name", string.Concat(Enumerable.Repeat("😀", 127)) + "  ", token);
            var before = await SnapshotAsync(db, token);
            var lockTimeout = await ScalarAsync(db, "SHOW lock_timeout", token);
            var statementTimeout = await ScalarAsync(db, "SHOW statement_timeout", token);
            await MigrateAsync(db, Target, token);
            var upgraded = await SnapshotAsync(db, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            await MigrateAsync(db, Target, token);
            Assert.Equal(upgraded, await SnapshotAsync(db, token));
            await MigrateAsync(db, Previous, token);
            var downgraded = await SnapshotAsync(db, token);
            AssertCompanyOnlyDowngradeRewrite(before, downgraded);
            await MigrateAsync(db, Target, token);
            var reupgraded = await SnapshotAsync(db, token);
            Assert.Equal(downgraded.Rows, reupgraded.Rows);
            Assert.Equal(upgraded.Schema, reupgraded.Schema);
            Assert.Equal(upgraded.Migrations, reupgraded.Migrations);
            Assert.Equal(lockTimeout, await ScalarAsync(db, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await ScalarAsync(db, "SHOW statement_timeout", token));
            Assert.Null(db.Database.CurrentTransaction);
        }, Previous);
    }

    /// <summary>Refuses incompatible retained values instead of silently normalizing existing records.</summary>
    /// <param name="field">The Company field with a retained supplementary overflow.</param>
    /// <returns>The unchanged row/schema/migration evidence after refusal.</returns>
    [Theory]
    [InlineData("Name")]
    [InlineData("TaxNumber")]
    [InlineData("Registrar")]
    public async Task RetainedOverflowRefusesUpgradeWithoutRewritingAnyRow(string field)
    {
        await RunAsync(async (db, token) =>
        {
            await SeedAsync(db, token);
            await SetAsync(db, field, string.Concat(Enumerable.Repeat("😀", 129)), token);
            await AssertRefusedAsync(db, Target, token);
        }, Previous);
    }

    /// <summary>Rejects incompatible preimages and ownership collisions before altering the table.</summary>
    /// <param name="drift">The independent schema precondition violation.</param>
    /// <returns>The exact rollback and session-cleanup checks.</returns>
    [Theory]
    [InlineData("type")]
    [InlineData("nullable")]
    [InlineData("default")]
    [InlineData("collision")]
    public async Task UnexpectedUpgradePreimageRefusesWithoutChangingRowsOrSchema(string drift)
    {
        await RunAsync(async (db, token) =>
        {
            await SeedAsync(db, token);
            await ExecuteAsync(db, drift switch
            {
                "type" => "ALTER TABLE public.\"Company\" ALTER COLUMN \"TaxNumber\" TYPE text",
                "nullable" => "ALTER TABLE public.\"Company\" ALTER COLUMN \"Name\" DROP NOT NULL",
                "default" => "ALTER TABLE public.\"Company\" ALTER COLUMN \"Registrar\" SET DEFAULT 'unexpected'",
                "collision" => "CREATE FUNCTION public.\"NormalizeCompanySource\"() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertRefusedAsync(db, Target, token);
        }, Previous);
    }

    /// <summary>Prevents downgrade from removing changed or foreign schema objects.</summary>
    /// <param name="drift">The changed constraint, function, or trigger custody.</param>
    /// <returns>The exact retained row/schema/session checks.</returns>
    [Theory]
    [InlineData("disabled-trigger")]
    [InlineData("function")]
    [InlineData("check")]
    [InlineData("trigger-columns")]
    public async Task ChangedOwnedObjectsRefuseDowngradeWithoutRemovingUnknownSchema(string drift)
    {
        await RunAsync(async (db, token) =>
        {
            await SeedAsync(db, token);
            await ExecuteAsync(db, drift switch
            {
                "disabled-trigger" => "ALTER TABLE public.\"Company\" DISABLE TRIGGER \"NormalizeCompanySource\"",
                "function" => "CREATE OR REPLACE FUNCTION public.\"NormalizeCompanySource\"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS 'BEGIN RETURN NEW; END;'",
                "check" => "ALTER TABLE public.\"Company\" DROP CONSTRAINT \"CK_Company_TaxNumber_SourceLength\"; ALTER TABLE public.\"Company\" ADD CONSTRAINT \"CK_Company_TaxNumber_SourceLength\" CHECK (true)",
                "trigger-columns" => """
                    DROP TRIGGER "NormalizeCompanySource" ON public."Company";
                    CREATE TRIGGER "NormalizeCompanySource" BEFORE INSERT OR UPDATE OF "Name" ON public."Company"
                        FOR EACH ROW EXECUTE FUNCTION public."NormalizeCompanySource"();
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertRefusedAsync(db, Previous, token);
        });
    }

    /// <summary>Enforces finite migration row admission without changing the accepted boundary.</summary>
    /// <returns>The 10,001-row refusal and exact 10,000-row acceptance checks.</returns>
    [Fact]
    public async Task RowAdmissionRefuses10001ThenAccepts10000WithoutRewriting()
    {
        await RunAsync(async (db, token) =>
        {
            await SeedAsync(db, token);
            await ExecuteAsync(db, "INSERT INTO public.\"Company\" (\"ID\",\"Name\") SELECT value,'Synthetic retained' FROM generate_series(3,10001) value", token);
            await AssertRefusedAsync(db, Target, token);
            await ExecuteAsync(db, "DELETE FROM public.\"Company\" WHERE \"ID\"=10001", token);
            var before = await SnapshotAsync(db, token);
            await MigrateAsync(db, Target, token);
            Assert.Equal(before.Rows, (await SnapshotAsync(db, token)).Rows);
        }, Previous);
    }

    /// <summary>Exercises unchanged older EF varchar mappings against the upgraded physical boundary.</summary>
    /// <param name="field">The original Company field.</param>
    /// <param name="update">Whether the old writer updates an existing row.</param>
    /// <returns>The tracked literal, stored prefix, and precise nonspace-overflow refusal checks.</returns>
    [Theory]
    [InlineData("Name", false)]
    [InlineData("Name", true)]
    [InlineData("TaxNumber", false)]
    [InlineData("TaxNumber", true)]
    [InlineData("Registrar", false)]
    [InlineData("Registrar", true)]
    public async Task PreviousEfMappingRetainsLiteralAndUsesPhysicalCapacity(string field, bool update)
    {
        await RunAsync(async (db, token) =>
        {
            await SeedAsync(db, token);
            await using var old = new PreviousCompanyContext(new DbContextOptionsBuilder<PreviousCompanyContext>()
                .UseNpgsql(db.Database.GetDbConnection(), contextOwnsConnection: false).Options);
            var prefix = string.Concat(Enumerable.Repeat("😀", 128));
            var literal = prefix + " ";
            var value = new PreviousCompany { Id = update ? 1 : 20, Name = "Other field" };
            old.Entry(value).Property(field).CurrentValue = literal;
            if (update)
            {
                old.Attach(value);
                old.Entry(value).Property(field).IsModified = true;
            }
            else old.Add(value);
            await old.SaveChangesAsync(token);
            Assert.Equal(literal, old.Entry(value).Property(field).CurrentValue);
            Assert.Equal(prefix, await ScalarAsync(db, $"SELECT \"{field}\" FROM public.\"Company\" WHERE \"ID\"={value.Id}", token));
            var before = await SnapshotAsync(db, token);
            if (!update)
            {
                value = new PreviousCompany { Id = 21, Name = "Other field" };
                old.Add(value);
            }
            old.Entry(value).Property(field).CurrentValue = string.Concat(Enumerable.Repeat("😀", 129));
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => old.SaveChangesAsync(token));
            var postgres = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal($"CK_Company_{field}_SourceLength", postgres.ConstraintName);
            Assert.Equal(before, await SnapshotAsync(db, token));
        });
    }

    private static void AssertCompanyOnlyDowngradeRewrite(Snapshot before, Snapshot after)
    {
        Assert.Equal(before.Schema, after.Schema);
        Assert.Equal(before.Migrations, after.Migrations);
        var expected = JsonNode.Parse(before.Rows!)!.AsObject();
        var actual = JsonNode.Parse(after.Rows!)!.AsObject();
        var companies = expected["companies"]!.AsArray();
        var actualCompanies = actual["companies"]!.AsArray();
        Assert.Equal(2, companies.Count);
        Assert.Equal(companies.Count, actualCompanies.Count);
        foreach (var entry in companies)
        {
            var row = Assert.IsType<JsonObject>(entry);
            var id = row["ID"]!.GetValue<int>();
            var current = Assert.IsType<JsonObject>(Assert.Single(actualCompanies,
                candidate => candidate!["ID"]!.GetValue<int>() == id));
            var originalXmin = row["xmin"]!.GetValue<string>();
            Assert.NotEqual(originalXmin, current["xmin"]!.GetValue<string>());
            // PG18 text-to-varchar rollback rewrites only Company row versions; preserve all other fields and row versions.
            current["xmin"] = originalXmin;
        }
        Assert.True(JsonNode.DeepEquals(expected, actual));
    }
    private async Task RunAsync(Func<CustomerDbContext, CancellationToken, Task> action, string? target = null)
    {
        await using var source = fixture.Context();
        var connection = new NpgsqlConnectionStringBuilder(source.Database.GetDbConnection().ConnectionString)
        {
            Database = "company_capacity_" + Guid.NewGuid().ToString("N"),
            Pooling = false,
        };
        await using var db = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(connection.ConnectionString).Options);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Exception? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            db.Database.SetCommandTimeout(30);
            await db.Database.MigrateAsync(lifetime.Token);
            await db.Database.OpenConnectionAsync(lifetime.Token);
            if (target is not null) await MigrateAsync(db, target, lifetime.Token);
            await action(db, lifetime.Token);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await db.Database.CloseConnectionAsync();
                await db.Database.EnsureDeletedAsync(cleanup.Token);
                Assert.False(await db.Database.CanConnectAsync(cleanup.Token));
            }
            catch (Exception error) { cleanupFailure = error; }
        }
        if (primary is not null && cleanupFailure is not null) throw new AggregateException(primary, cleanupFailure);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static Task MigrateAsync(CustomerDbContext db, string target, CancellationToken token) => db.GetService<IMigrator>().MigrateAsync(target, token);

    private static async Task AssertRefusedAsync(CustomerDbContext db, string target, CancellationToken token)
    {
        var before = await SnapshotAsync(db, token);
        var lockTimeout = await ScalarAsync(db, "SHOW lock_timeout", token);
        var statementTimeout = await ScalarAsync(db, "SHOW statement_timeout", token);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(db, target, token));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await SnapshotAsync(db, token));
        Assert.Equal(lockTimeout, await ScalarAsync(db, "SHOW lock_timeout", token));
        Assert.Equal(statementTimeout, await ScalarAsync(db, "SHOW statement_timeout", token));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal("True", await ScalarAsync(db, "SELECT to_regclass('pg_temp.\"__CompanySourceGuard\"') IS NULL", token));
    }

    private static Task SeedAsync(CustomerDbContext db, CancellationToken token) => ExecuteAsync(db, """
        INSERT INTO public."Company" ("ID","Name","TaxNumber","Registrar") VALUES (1,'Target','  Tax  ','  Registrar  '),(2,'Retained',NULL,NULL);
        INSERT INTO public."Address" ("ID","AddressLine1","CountryID") VALUES (1,'Retained address',764);
        INSERT INTO public."Customer" ("ID","FirstName","LastName","Email","CompanyID","BillingAddressID","ShippingAddressID")
            VALUES (1,'Synthetic','Retained','retained@example.invalid',2,1,1);
        """, token);

    private static async Task SetAsync(CustomerDbContext db, string field, string value, CancellationToken token)
    {
        Assert.Contains(field, new[] { "Name", "TaxNumber", "Registrar" });
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = $"UPDATE public.\"Company\" SET \"{field}\"=@value WHERE \"ID\"=1";
        command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = value, Size = -1 });
        Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
    }

    private static async Task ExecuteAsync(CustomerDbContext db, string sql, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string?> ScalarAsync(CustomerDbContext db, string sql, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record Snapshot(string? Rows, string? Schema, string Migrations);

    private static async Task<Snapshot> SnapshotAsync(CustomerDbContext db, CancellationToken token) => new(
        await ScalarAsync(db, """
            SELECT jsonb_build_object(
                'companies',(SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin',c.xmin::text) ORDER BY c."ID") FROM public."Company" c),
                'customers',(SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin',c.xmin::text) ORDER BY c."ID") FROM public."Customer" c),
                'addresses',(SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin',c.xmin::text) ORDER BY c."ID") FROM public."Address" c),
                'replay',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c."Key") FROM public."CustomerCreateOperation" c),
                'completionReplay',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c."CustomerId",c."Key") FROM public."QuotationProfileCompletionOperation" c))::text
            """, token),
        await ScalarAsync(db, """
            SELECT jsonb_build_object(
                'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE c.table_schema='public'),
                'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated) ORDER BY c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace),
                'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname) FROM pg_indexes WHERE schemaname='public'),
                'triggers',(SELECT jsonb_agg(jsonb_build_array(t.tgname,pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgname) FROM pg_trigger t WHERE t.tgrelid='public."Company"'::regclass AND NOT t.tgisinternal),
                'functions',(SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.proname) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname='NormalizeCompanySource'))::text
            """, token),
        JsonSerializer.Serialize(await db.Database.GetAppliedMigrationsAsync(token)));

    private sealed class PreviousCompany
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? TaxNumber { get; set; }
        public string? Registrar { get; set; }
    }

    private sealed class PreviousCompanyContext(DbContextOptions<PreviousCompanyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var company = builder.Entity<PreviousCompany>();
            company.ToTable("Company");
            company.HasKey(value => value.Id);
            company.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            company.Property(value => value.Name).IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            company.Property(value => value.TaxNumber).HasMaxLength(256).HasColumnType("character varying(256)");
            company.Property(value => value.Registrar).HasMaxLength(256).HasColumnType("character varying(256)");
        }
    }
}
