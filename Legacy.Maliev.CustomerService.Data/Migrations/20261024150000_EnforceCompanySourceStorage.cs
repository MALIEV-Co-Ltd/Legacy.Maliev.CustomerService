using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.CustomerService.Data.Migrations;

/// <inheritdoc />
public partial class EnforceCompanySourceStorage : Migration
{
    private const string TriggerBody = """
        DECLARE field_values text[] := ARRAY[NEW."Name", NEW."TaxNumber", NEW."Registrar"];
            field_index integer; units integer; character_index integer; width integer; scalar text;
        BEGIN
            FOR field_index IN 1..3 LOOP
                IF field_values[field_index] IS NULL OR
                    char_length(field_values[field_index]) + char_length(regexp_replace(field_values[field_index] COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256 THEN
                    CONTINUE;
                END IF;
                units := 0; character_index := 0;
                WHILE units < 256 LOOP
                    scalar := substr(field_values[field_index], character_index + 1, 1);
                    IF scalar = '' THEN EXIT; END IF;
                    width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                    IF units + width > 256 THEN EXIT; END IF;
                    units := units + width;
                    character_index := character_index + 1;
                END LOOP;
                IF btrim(substr(field_values[field_index], character_index + 1), ' ') = '' THEN
                    field_values[field_index] := substr(field_values[field_index], 1, character_index);
                END IF;
            END LOOP;
            NEW."Name" := field_values[1];
            NEW."TaxNumber" := field_values[2];
            NEW."Registrar" := field_values[3];
            RETURN NEW;
        END;
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Company" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR (SELECT count(*) FROM pg_attribute WHERE attrelid = 'public."Company"'::regclass
                        AND attname IN ('Name','TaxNumber','Registrar') AND NOT attisdropped
                        AND attnotnull = (attname = 'Name') AND atttypid = 'varchar'::regtype AND atttypmod = 260) <> 3
                    OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                        WHERE d.adrelid = 'public."Company"'::regclass AND a.attname IN ('Name','TaxNumber','Registrar') AND NOT a.attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."Company"'::regclass
                        AND conname IN ('CK_Company_Name_SourceLength','CK_Company_TaxNumber_SourceLength','CK_Company_Registrar_SourceLength'))
                    OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."Company"'::regclass AND tgname = 'NormalizeCompanySource')
                    OR EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'NormalizeCompanySource') THEN
                    RAISE EXCEPTION 'Company capacity upgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Company" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Company capacity upgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Company" WHERE
                    char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 256 OR
                    char_length("TaxNumber") + char_length(regexp_replace("TaxNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 256 OR
                    char_length("Registrar") + char_length(regexp_replace("Registrar" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 256) THEN
                    RAISE EXCEPTION 'Retained Company fields exceed source capacity.';
                END IF;
            END $guard$;
            DO $types$
            DECLARE alterations text; column_count integer;
            BEGIN
                SELECT string_agg(format('ALTER COLUMN %I TYPE text COLLATE %I.%I',
                    a.attname,n.nspname,c.collname),', ' ORDER BY a.attnum),count(*)
                INTO alterations,column_count
                FROM pg_attribute a JOIN pg_collation c ON c.oid=a.attcollation
                    JOIN pg_namespace n ON n.oid=c.collnamespace
                WHERE a.attrelid='public."Company"'::regclass AND NOT a.attisdropped
                    AND a.attname IN ('Name','TaxNumber','Registrar');
                IF column_count <> 3 OR alterations IS NULL THEN
                    RAISE EXCEPTION 'Company capacity collation metadata is incompatible.';
                END IF;
                EXECUTE 'ALTER TABLE public."Company" ' || alterations;
            END $types$;
            ALTER TABLE public."Company" ADD CONSTRAINT "CK_Company_Name_SourceLength" CHECK
                (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256) NOT VALID;
            ALTER TABLE public."Company" ADD CONSTRAINT "CK_Company_TaxNumber_SourceLength" CHECK
                (char_length("TaxNumber") + char_length(regexp_replace("TaxNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256) NOT VALID;
            ALTER TABLE public."Company" ADD CONSTRAINT "CK_Company_Registrar_SourceLength" CHECK
                (char_length("Registrar") + char_length(regexp_replace("Registrar" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256) NOT VALID;
            ALTER TABLE public."Company" VALIDATE CONSTRAINT "CK_Company_Name_SourceLength",
                VALIDATE CONSTRAINT "CK_Company_TaxNumber_SourceLength", VALIDATE CONSTRAINT "CK_Company_Registrar_SourceLength";
            CREATE FUNCTION public."NormalizeCompanySource"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $source$
            """ + TriggerBody + """
            $source$;
            CREATE TRIGGER "NormalizeCompanySource" BEFORE INSERT OR UPDATE OF "Name","TaxNumber","Registrar" ON public."Company"
                FOR EACH ROW EXECUTE FUNCTION public."NormalizeCompanySource"();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Company" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            DECLARE field_name text; definition text; expected text;
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR (SELECT count(*) FROM pg_attribute WHERE attrelid = 'public."Company"'::regclass
                        AND attname IN ('Name','TaxNumber','Registrar') AND NOT attisdropped
                        AND attnotnull = (attname = 'Name') AND atttypid = 'text'::regtype) <> 3
                    OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                        WHERE d.adrelid = 'public."Company"'::regclass AND a.attname IN ('Name','TaxNumber','Registrar') AND NOT a.attisdropped) THEN
                    RAISE EXCEPTION 'Company capacity downgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Company" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Company capacity downgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Company" WHERE
                    char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 256 OR
                    char_length("TaxNumber") + char_length(regexp_replace("TaxNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 256 OR
                    char_length("Registrar") + char_length(regexp_replace("Registrar" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 256) THEN
                    RAISE EXCEPTION 'Retained Company fields exceed source capacity.';
                END IF;
                CREATE TEMP TABLE "__CompanySourceGuard" ("Name" text, "TaxNumber" text, "Registrar" text,
                    CONSTRAINT "Name" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256),
                    CONSTRAINT "TaxNumber" CHECK (char_length("TaxNumber") + char_length(regexp_replace("TaxNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256),
                    CONSTRAINT "Registrar" CHECK (char_length("Registrar") + char_length(regexp_replace("Registrar" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256)) ON COMMIT DROP;
                FOREACH field_name IN ARRAY ARRAY['Name','TaxNumber','Registrar'] LOOP
                    SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                        WHERE conrelid = 'pg_temp."__CompanySourceGuard"'::regclass AND conname = field_name;
                    SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                        WHERE conrelid = 'public."Company"'::regclass AND conname = 'CK_Company_' || field_name || '_SourceLength'
                        AND contype = 'c' AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                        AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Company"'::regclass AND attname = field_name)]::smallint[];
                    IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                        RAISE EXCEPTION 'Company capacity downgrade constraint is incompatible.';
                    END IF;
                END LOOP;
                IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE oid = to_regprocedure('public."NormalizeCompanySource"()')
                    AND prorettype = 'trigger'::regtype AND prolang = (SELECT oid FROM pg_language WHERE lanname = 'plpgsql')
                    AND NOT prosecdef AND NOT proisstrict AND NOT proleakproof AND prokind = 'f' AND proparallel = 'u' AND provolatile = 'v'
                    AND proconfig = ARRAY['search_path=pg_catalog'] AND prosrc = $source$
            """ + TriggerBody + """
            $source$) OR NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."Company"'::regclass
                    AND tgname = 'NormalizeCompanySource' AND tgfoid = to_regprocedure('public."NormalizeCompanySource"()')
                    AND tgtype = 23 AND tgenabled = 'O' AND NOT tgisinternal AND tgconstraint = 0 AND tgqual IS NULL
                    AND tgnargs = 0 AND octet_length(tgargs) = 0
                    AND tgattr::text = (SELECT string_agg(attnum::text,' ' ORDER BY array_position(ARRAY['Name','TaxNumber','Registrar'],attname::text))
                        FROM pg_attribute WHERE attrelid = 'public."Company"'::regclass AND attname IN ('Name','TaxNumber','Registrar') AND NOT attisdropped)) THEN
                    RAISE EXCEPTION 'Company capacity downgrade normalizer is incompatible.';
                END IF;
                DROP TABLE pg_temp."__CompanySourceGuard";
            END $guard$;
            DROP TRIGGER "NormalizeCompanySource" ON public."Company";
            DROP FUNCTION public."NormalizeCompanySource"();
            ALTER TABLE public."Company" DROP CONSTRAINT "CK_Company_Name_SourceLength",
                DROP CONSTRAINT "CK_Company_TaxNumber_SourceLength", DROP CONSTRAINT "CK_Company_Registrar_SourceLength";
            DO $types$
            DECLARE alterations text; column_count integer;
            BEGIN
                SELECT string_agg(format('ALTER COLUMN %I TYPE character varying(256) COLLATE %I.%I',
                    a.attname,n.nspname,c.collname),', ' ORDER BY a.attnum),count(*)
                INTO alterations,column_count
                FROM pg_attribute a JOIN pg_collation c ON c.oid=a.attcollation
                    JOIN pg_namespace n ON n.oid=c.collnamespace
                WHERE a.attrelid='public."Company"'::regclass AND NOT a.attisdropped
                    AND a.attname IN ('Name','TaxNumber','Registrar');
                IF column_count <> 3 OR alterations IS NULL THEN
                    RAISE EXCEPTION 'Company capacity collation metadata is incompatible.';
                END IF;
                EXECUTE 'ALTER TABLE public."Company" ' || alterations;
            END $types$;
            """);
    }
}
