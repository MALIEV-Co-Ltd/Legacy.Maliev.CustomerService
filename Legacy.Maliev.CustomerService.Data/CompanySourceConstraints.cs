namespace Legacy.Maliev.CustomerService.Data;

internal static class CompanySourceConstraints
{
    internal const string NameLengthSql = """
        char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256
        """;
    internal const string TaxNumberLengthSql = """
        char_length("TaxNumber") + char_length(regexp_replace("TaxNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256
        """;
    internal const string RegistrarLengthSql = """
        char_length("Registrar") + char_length(regexp_replace("Registrar" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 256
        """;
}
