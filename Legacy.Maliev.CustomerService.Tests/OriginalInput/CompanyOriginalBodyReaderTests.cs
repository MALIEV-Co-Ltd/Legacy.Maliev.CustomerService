using System.Reflection;
using System.Text;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Tests.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stj = System.Text.Json.JsonSerializer;

namespace Legacy.Maliev.CustomerService.Tests.OriginalInput;

/// <summary>Compares complete public source-shaped company bodies with native and candidate MVC readers before consumed-field projection.</summary>
/// <param name="fixture">The shared authority fixture used to create candidate hosts and reconstruct native MVC formatter options.</param>
[Collection("Customer original body")]
public sealed class CompanyOriginalBodyReaderTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    /// <summary>Provides company body cases for grammar, scalars, metadata, nested graphs, reference handling, and null history.</summary>
    /// <returns>Theory rows containing a case label, exact JSON, source-shaped outcome, consumed three-field projection, and optional native outcome.</returns>
    public static IEnumerable<object?[]> Cases()
    {
        yield return ["ordinary", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["empty object", "{}", "success", "[null,null,null]", "success"];
        yield return ["single quotes", "{'Name':'  Company envelope  ','TaxNumber':'123000','Registrar':'Office'}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["unquoted names", "{Name:\"  Company envelope  \",TaxNumber:\"123000\",Registrar:\"Office\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["leading comment", "/* company */{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["member comment", "{/* company */\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["trailing comma", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["additional document", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\"}{\"Id\":\"private-company-input-marker\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["trailing token", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\"} private-company-input-marker", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["whole null", "null", "no-value", "[null,null,null]", "no-value"];
        yield return ["empty body", "", "no-value", "[null,null,null]", "no-value"];
        yield return ["whitespace", "  ", "no-value", "[null,null,null]", "failure"];
        yield return ["empty string root", "\"\"", "no-value", "[null,null,null]", "failure"];
        yield return ["wrong string", "\"wrong\"", "failure", "[null,null,null]", "failure"];
        yield return ["array", "[]", "failure", "[null,null,null]", "failure"];
        yield return ["unresolved ref", "{\"$ref\":\"missing\"}", "no-value", "[null,null,null]", "success"];
        yield return ["invalid ref", "{\"$ref\":42}", "failure", "[null,null,null]", "success"];
        yield return ["ref plus scalar", "{\"$ref\":\"missing\",\"Id\":1}", "failure", "[null,null,null]", "success"];
        yield return ["wrong values", "{\"$values\":[]}", "failure", "[null,null,null]", "success"];
        yield return ["exact source type-null exception", "{\"$type\":null}", "throw:System.NullReferenceException", "[null,null,null]", "success"];
        yield return ["unknown skipped", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Unknown\":{\"arbitrary\":[true,{}]}}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["type activation disabled", "{\"$type\":\"System.IO.FileInfo, System.Private.CoreLib\",\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["Name literal padded", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":\"  ไทย  \"}", "success", "[\"  ไทย  \",\"123000\",\"Office\"]", null];
        yield return ["Name empty", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":\"\"}", "success", "[\"\",\"123000\",\"Office\"]", null];
        yield return ["Name integer", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":123}", "success", "[\"123\",\"123000\",\"Office\"]", null];
        yield return ["Name boolean", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":true}", "success", "[\"true\",\"123000\",\"Office\"]", null];
        yield return ["Name decimal", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":-12.50}", "success", "[\"-12.50\",\"123000\",\"Office\"]", null];
        yield return ["Name exponent", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":1e+02}", "success", "[\"1e+02\",\"123000\",\"Office\"]", null];
        yield return ["Name huge exponent", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":1e400}", "success", "[\"1e400\",\"123000\",\"Office\"]", null];
        yield return ["Name big integer", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":123456789012345678901234567890}", "success", "[\"123456789012345678901234567890\",\"123000\",\"Office\"]", null];
        yield return ["Name lone high", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":\"\\ud800\"}", "success", "[\"�\",\"123000\",\"Office\"]", null];
        yield return ["Name lone low", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":\"\\udfff\"}", "success", "[\"�\",\"123000\",\"Office\"]", null];
        yield return ["Name valid replacement", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":\"\\ufffd\"}", "success", "[\"�\",\"123000\",\"Office\"]", null];
        yield return ["Name duplicate last null", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":\" keep \",\"Name\":null}", "success", "[\" keep \",\"123000\",\"Office\"]", "success"];
        yield return ["Name null then value", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":null,\"Name\":\" keep \"}", "success", "[\" keep \",\"123000\",\"Office\"]", "success"];
        yield return ["Name invalid {}", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":{}}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["Name invalid []", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Name\":[]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["TaxNumber literal padded", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":\"  ไทย  \"}", "success", "[\"  Company envelope  \",\"  ไทย  \",\"Office\"]", null];
        yield return ["TaxNumber empty", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":\"\"}", "success", "[\"  Company envelope  \",\"\",\"Office\"]", null];
        yield return ["TaxNumber integer", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":123}", "success", "[\"  Company envelope  \",\"123\",\"Office\"]", null];
        yield return ["TaxNumber boolean", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":true}", "success", "[\"  Company envelope  \",\"true\",\"Office\"]", null];
        yield return ["TaxNumber decimal", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":-12.50}", "success", "[\"  Company envelope  \",\"-12.50\",\"Office\"]", null];
        yield return ["TaxNumber exponent", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":1e+02}", "success", "[\"  Company envelope  \",\"1e+02\",\"Office\"]", null];
        yield return ["TaxNumber huge exponent", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":1e400}", "success", "[\"  Company envelope  \",\"1e400\",\"Office\"]", null];
        yield return ["TaxNumber big integer", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":123456789012345678901234567890}", "success", "[\"  Company envelope  \",\"123456789012345678901234567890\",\"Office\"]", null];
        yield return ["TaxNumber lone high", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":\"\\ud800\"}", "success", "[\"  Company envelope  \",\"�\",\"Office\"]", null];
        yield return ["TaxNumber lone low", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":\"\\udfff\"}", "success", "[\"  Company envelope  \",\"�\",\"Office\"]", null];
        yield return ["TaxNumber valid replacement", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":\"\\ufffd\"}", "success", "[\"  Company envelope  \",\"�\",\"Office\"]", null];
        yield return ["TaxNumber duplicate last null", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":\" keep \",\"TaxNumber\":null}", "success", "[\"  Company envelope  \",\" keep \",\"Office\"]", "success"];
        yield return ["TaxNumber null then value", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":null,\"TaxNumber\":\" keep \"}", "success", "[\"  Company envelope  \",\" keep \",\"Office\"]", "success"];
        yield return ["TaxNumber invalid {}", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":{}}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["TaxNumber invalid []", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"TaxNumber\":[]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["Registrar literal padded", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":\"  ไทย  \"}", "success", "[\"  Company envelope  \",\"123000\",\"  ไทย  \"]", null];
        yield return ["Registrar empty", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":\"\"}", "success", "[\"  Company envelope  \",\"123000\",\"\"]", null];
        yield return ["Registrar integer", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":123}", "success", "[\"  Company envelope  \",\"123000\",\"123\"]", null];
        yield return ["Registrar boolean", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":true}", "success", "[\"  Company envelope  \",\"123000\",\"true\"]", null];
        yield return ["Registrar decimal", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":-12.50}", "success", "[\"  Company envelope  \",\"123000\",\"-12.50\"]", null];
        yield return ["Registrar exponent", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":1e+02}", "success", "[\"  Company envelope  \",\"123000\",\"1e+02\"]", null];
        yield return ["Registrar huge exponent", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":1e400}", "success", "[\"  Company envelope  \",\"123000\",\"1e400\"]", null];
        yield return ["Registrar big integer", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":123456789012345678901234567890}", "success", "[\"  Company envelope  \",\"123000\",\"123456789012345678901234567890\"]", null];
        yield return ["Registrar lone high", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":\"\\ud800\"}", "success", "[\"  Company envelope  \",\"123000\",\"�\"]", null];
        yield return ["Registrar lone low", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":\"\\udfff\"}", "success", "[\"  Company envelope  \",\"123000\",\"�\"]", null];
        yield return ["Registrar valid replacement", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":\"\\ufffd\"}", "success", "[\"  Company envelope  \",\"123000\",\"�\"]", null];
        yield return ["Registrar duplicate last null", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":\" keep \",\"Registrar\":null}", "success", "[\"  Company envelope  \",\"123000\",\" keep \"]", "success"];
        yield return ["Registrar null then value", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":null,\"Registrar\":\" keep \"}", "success", "[\"  Company envelope  \",\"123000\",\" keep \"]", "success"];
        yield return ["Registrar invalid {}", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":{}}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["Registrar invalid []", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Registrar\":[]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "failure"];
        yield return ["ignored Id invalid object", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":{\"private-company-input-marker\":1}}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id earlier bad then corrected", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":{},\"Id\":1}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored CreatedDate invalid object", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"CreatedDate\":{\"private-company-input-marker\":1}}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored CreatedDate earlier bad then corrected", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"CreatedDate\":{},\"CreatedDate\":\"2000-01-01\"}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored ModifiedDate invalid object", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"ModifiedDate\":{\"private-company-input-marker\":1}}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored ModifiedDate earlier bad then corrected", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"ModifiedDate\":{},\"ModifiedDate\":\"2000-01-01\"}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id valid 1", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":1}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id valid -2147483648", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":-2147483648}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id valid 2147483647", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":2147483647}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id valid \" +17 \"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":\" +17 \"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id valid null", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":null}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id valid \"\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":\"\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id invalid true", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":true}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id invalid []", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":[]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id invalid 7.0", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":7.0}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id invalid 2147483648", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":2147483648}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ignored Id invalid \" \"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":\" \"}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["CreatedDate valid \"2000-01-02T03:04:05Z\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"CreatedDate\":\"2000-01-02T03:04:05Z\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["CreatedDate valid \"/Date(0)/\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"CreatedDate\":\"/Date(0)/\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["CreatedDate valid \"2000-01-01T24:00:00Z\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"CreatedDate\":\"2000-01-01T24:00:00Z\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["CreatedDate valid null", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"CreatedDate\":null}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["CreatedDate valid \"\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"CreatedDate\":\"\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ModifiedDate valid \"2000-01-02T03:04:05Z\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"ModifiedDate\":\"2000-01-02T03:04:05Z\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ModifiedDate valid \"/Date(0)/\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"ModifiedDate\":\"/Date(0)/\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ModifiedDate valid \"2000-01-01T24:00:00Z\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"ModifiedDate\":\"2000-01-01T24:00:00Z\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ModifiedDate valid null", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"ModifiedDate\":null}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["ModifiedDate valid \"\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"ModifiedDate\":\"\"}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested company.Id", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":{\"Id\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested company.Name", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":{\"Name\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested company.TaxNumber", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":{\"TaxNumber\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested company.Registrar", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":{\"Registrar\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested company.CreatedDate", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":{\"CreatedDate\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested company.ModifiedDate", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":{\"ModifiedDate\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized navigation company.Customer", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":{\"Customer\":42}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.Id", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Id\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.FirstName", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"FirstName\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.LastName", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"LastName\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.FullName", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"FullName\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.Telephone", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Telephone\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.Mobile", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Mobile\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.Fax", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Fax\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.Email", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Email\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.DateOfBirth", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"DateOfBirth\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.CompanyId", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"CompanyId\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.BillingAddressId", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddressId\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.ShippingAddressId", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"ShippingAddressId\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.CreatedDate", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"CreatedDate\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested customer.ModifiedDate", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"ModifiedDate\":{\"private-company-input-marker\":1}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized navigation customer.BillingAddress", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":42}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized navigation customer.Company", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"Company\":42}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized navigation customer.ShippingAddress", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"ShippingAddress\":42}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.Id", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"Id\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.Building", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"Building\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.AddressLine1", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"AddressLine1\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.AddressLine2", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"AddressLine2\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.City", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"City\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.State", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"State\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.PostalCode", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"PostalCode\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.CountryId", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"CountryId\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.CreatedDate", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"CreatedDate\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized nested address.ModifiedDate", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"ModifiedDate\":{\"private-company-input-marker\":1}}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized navigation address.CustomerBillingAddress", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"CustomerBillingAddress\":42}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["recognized navigation address.CustomerShippingAddress", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"CustomerShippingAddress\":42}}]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["root Customer collection {}", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":{}}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["root Customer collection 42", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":42}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["root Customer collection \"wrong\"", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":\"wrong\"}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["invalid graph before duplicate valid", "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Customer\":[{\"BillingAddress\":{\"CountryId\":{}}}],\"Customer\":[]}", "failure", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
        yield return ["complete recursive graph", "{\"$id\":\"K\",\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":999,\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\",\"Customer\":[{\"$id\":\"C\",\"Id\":998,\"FirstName\":\"NeverPersist\",\"LastName\":\"Graph\",\"FullName\":\"forged full\",\"Telephone\":\"T\",\"Mobile\":\"M\",\"Fax\":\"F\",\"Email\":\"nested@example.test\",\"DateOfBirth\":\"2000-01-01\",\"CompanyId\":999,\"BillingAddressId\":997,\"ShippingAddressId\":997,\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\",\"Company\":{\"$ref\":\"K\"},\"BillingAddress\":{\"$id\":\"A\",\"Id\":997,\"Building\":\"Nested\",\"AddressLine1\":\"forged road\",\"AddressLine2\":\"Line2\",\"City\":\"Bangkok\",\"State\":\"State\",\"PostalCode\":\"10100\",\"CountryId\":764,\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\",\"CustomerBillingAddress\":[{\"$ref\":\"C\"}],\"CustomerShippingAddress\":[{\"$ref\":\"C\"}]},\"ShippingAddress\":{\"$ref\":\"A\"}}]}", "success", "[\"  Company envelope  \",\"123000\",\"Office\"]", "success"];
    }

    /// <summary>Checks that complete source-shaped company validation occurs before projecting only name, tax number, and registrar.</summary>
    /// <param name="label">The descriptive theory-case label retained for case identification.</param>
    /// <param name="json">The exact company JSON submitted to the public source-shaped, native, and candidate readers.</param>
    /// <param name="expectedKind">The expected source-shaped and candidate outcome, including success, failure, no-value, or a named thrown exception.</param>
    /// <param name="expectedProjection">The serialized array of expected Name, TaxNumber, and Registrar values on a successful source-shaped or candidate read.</param>
    /// <param name="nativeKind">The expected native MVC outcome when specified; null leaves the native outcome unasserted.</param>
    /// <returns>A task that completes after asserting source-shaped and candidate outcomes and projections, optional native outcome, and sanitized candidate failure keys and messages.</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CompleteCompanySourceShape_IsParsedBeforeProjectingOnlyThreeFields(
        string label, string json, string expectedKind, string expectedProjection, string? nativeKind)
    {
        var source = await CustomerOriginalInputOracle.ReadAsync("company", json);
        Assert.Equal(expectedKind, source.Kind);
        if (source.Kind == "success")
            Assert.Equal(Stj.Deserialize<string?[]>(expectedProjection), CompanyOriginalBodyHarness.Values(source.Model));

        await using var host = fixture.Start();
        var nativeOptions = CustomerOriginalBodyNative.Create(host.Services);
        var candidateOptions = host.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        foreach (var update in new[] { false, true })
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var native = await CompanyOriginalBodyHarness.ReadAsync(host.Services, nativeOptions, bytes, update);
            if (nativeKind is not null) Assert.Equal(nativeKind, native.Kind);
            var candidate = await CompanyOriginalBodyHarness.ReadAsync(host.Services, candidateOptions, bytes, update);
            Assert.Equal(expectedKind, candidate.Kind);
            if (candidate.Kind == "success")
                Assert.Equal(Stj.Deserialize<string?[]>(expectedProjection), CompanyOriginalBodyHarness.Values(candidate.Model));
            if (candidate.Kind == "failure")
            {
                Assert.True(candidate.ErrorCount > 0);
                Assert.DoesNotContain(candidate.Keys, value => value.Contains("private-company-input-marker", StringComparison.Ordinal));
                Assert.DoesNotContain(candidate.Messages, value => value.Contains("private-company-input-marker", StringComparison.Ordinal));
            }
        }
        _ = label;
    }

    /// <summary>Checks that complete company bodies retain a maximum nesting depth of 32.</summary>
    /// <param name="depth">The nesting depth of the synthetic unknown JSON member.</param>
    /// <param name="expectedKind">The expected success or failure outcome for that nesting depth.</param>
    /// <returns>A task that completes after asserting matching public source-shaped and candidate depth outcomes.</returns>
    [Theory]
    [InlineData(31, "success")]
    [InlineData(32, "success")]
    [InlineData(33, "failure")]
    public async Task CompanyOriginalDepthBudget_Remains32(int depth, string expectedKind)
    {
        var body = CustomerOriginalInputOracle.NestedUnknown(depth);
        Assert.Equal(expectedKind, (await CustomerOriginalInputOracle.ReadAsync("company", body)).Kind);
        await using var host = fixture.Start();
        var options = host.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        Assert.Equal(expectedKind, (await CompanyOriginalBodyHarness.ReadAsync(
            host.Services, options, Encoding.UTF8.GetBytes(body))).Kind);
    }

    /// <summary>Checks retained company JSON media, charset handling, and safe unsupported-charset failures.</summary>
    /// <param name="contentType">The complete request media type and optional charset used for the company body.</param>
    /// <param name="unicode">Whether to encode the valid company body as little-endian UTF-16 instead of UTF-8.</param>
    /// <param name="expected">The expected success, failure, or unsupported media outcome.</param>
    /// <returns>A task that completes after asserting public source-shaped and candidate outcomes, successful consumed values, and sanitized unsupported-charset errors.</returns>
    [Theory]
    [InlineData("application/json", false, "success")]
    [InlineData("text/json", false, "success")]
    [InlineData("application/vnd.synthetic+json", false, "success")]
    [InlineData("application/json; charset=utf-16", true, "success")]
    [InlineData("application/json; charset=x-private-company-charset", false, "failure")]
    [InlineData("application/json; charset=utf-16BE", false, "failure")]
    [InlineData("application/xml", false, "unsupported")]
    public async Task CompanyStockMediaCharsetAndSafeFailure_AreRetained(string contentType, bool unicode, string expected)
    {
        var bytes = (unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(CompanyOriginalBodyHarness.Standard);
        var source = await CustomerOriginalInputOracle.ReadBytesAsync(CustomerOriginalInputOracle.Formatter(),
            typeof(SourceCompany), new EmptyModelMetadataProvider(), bytes, contentType);
        Assert.Equal(expected, source.Kind);
        await using var host = fixture.Start();
        var candidate = await CompanyOriginalBodyHarness.ReadAsync(host.Services,
            host.Services.GetRequiredService<IOptions<MvcOptions>>().Value, bytes, contentType: contentType);
        Assert.Equal(expected, candidate.Kind);
        if (expected == "success")
            Assert.Equal(new[] { "  Company envelope  ", "123000", "Office" }, CompanyOriginalBodyHarness.Values(candidate.Model));
        if (expected == "failure")
        {
            Assert.Contains(typeof(UnsupportedContentTypeException).FullName!, candidate.ExceptionTypes);
            Assert.DoesNotContain(candidate.Messages, value => value.Contains("x-private-company-charset", StringComparison.Ordinal));
        }
    }

    /// <summary>Checks source-shaped company graph references and per-request reference and null-state isolation.</summary>
    /// <returns>A task that completes after asserting source graph identity, candidate graph acceptance, unresolved next-request references, and empty-object null projection.</returns>
    [Fact]
    public async Task OriginalCompanyReferencesAndNullHistory_DoNotCrossRequestBoundaries()
    {
        var graph = await CustomerOriginalInputOracle.ReadAsync("company", CompanyOriginalBodyHarness.Graph);
        var company = Assert.IsType<SourceCompany>(graph.Model);
        Assert.IsType<HashSet<SourceCustomer>>(company.Customer);
        var customer = Assert.Single(company.Customer);
        Assert.Same(company, customer.Company);
        Assert.Same(customer.BillingAddress, customer.ShippingAddress);
        Assert.Same(customer, Assert.Single(customer.BillingAddress.CustomerBillingAddress));
        Assert.Same(customer, Assert.Single(customer.BillingAddress.CustomerShippingAddress));
        await using var host = fixture.Start();
        var options = host.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        Assert.Equal("success", (await CompanyOriginalBodyHarness.ReadAsync(host.Services, options,
            Encoding.UTF8.GetBytes(CompanyOriginalBodyHarness.Graph))).Kind);
        Assert.Equal("no-value", (await CompanyOriginalBodyHarness.ReadAsync(host.Services, options,
            Encoding.UTF8.GetBytes("{\"$ref\":\"K\"}"))).Kind);
        var empty = await CompanyOriginalBodyHarness.ReadAsync(host.Services, options, Encoding.UTF8.GetBytes("{}"));
        Assert.Equal("success", empty.Kind);
        Assert.Equal(new string?[] { null, null, null }, CompanyOriginalBodyHarness.Values(empty.Model));
    }

    /// <summary>Checks exact ordinary company action selection while keeping direct System.Text.Json and scoped relation contracts native.</summary>
    /// <returns>A task that completes after asserting owned selection for ordinary actions, fallthrough for unknown and scoped actions, and unchanged direct serialization.</returns>
    [Fact]
    public async Task CompanySelection_IsExactOrdinaryActionAndLeavesDirectStjAndScopedContractsNative()
    {
        await using var host = fixture.Start();
        var options = host.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var owned = Assert.Single(options.InputFormatters, item =>
            item.GetType().Assembly == typeof(Program).Assembly &&
            item.GetType().FullName == CustomerOriginalBodyNative.FormatterName);
        foreach (var update in new[] { false, true })
        {
            var context = CompanyOriginalBodyHarness.Context(host.Services,
                Encoding.UTF8.GetBytes(CompanyOriginalBodyHarness.Standard), update);
            using var input = context.HttpContext.Request.Body;
            Assert.True(owned.CanRead(context));
            var endpoint = context.HttpContext.GetEndpoint();
            context.HttpContext.SetEndpoint(null);
            Assert.False(owned.CanRead(context));
            context.HttpContext.SetEndpoint(endpoint);
            context.HttpContext.Request.Method = "DELETE";
            Assert.False(owned.CanRead(context));
        }

        var scoped = CompanyOriginalBodyHarness.Context(host.Services,
            Encoding.UTF8.GetBytes(CompanyOriginalBodyHarness.Standard));
        using var scopedBody = scoped.HttpContext.Request.Body;
        scoped.HttpContext.Request.Method = "PUT";
        scoped.HttpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            new ControllerActionDescriptor
            {
                ControllerTypeInfo = typeof(CustomerRelationsController).GetTypeInfo(),
                MethodInfo = typeof(CustomerRelationsController).GetMethod(nameof(CustomerRelationsController.UpdateCompanyAsync))!
            }), "scoped-company-control"));
        Assert.False(owned.CanRead(scoped));
        var json = host.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var direct = Stj.Deserialize<UpsertCompanyRequest>(
            "{\"Name\":\"Native\",\"Id\":{},\"CreatedDate\":[],\"Customer\":42}", json);
        Assert.NotNull(direct);
        Assert.Equal("Native", direct.Name);
        Assert.Equal("{\"Name\":\"Native\"}", Stj.Serialize(direct, json));
    }
}

internal static class CompanyOriginalBodyHarness
{
    internal const string Standard = "{\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\"}";
    internal const string Graph = "{\"$id\":\"K\",\"Name\":\"  Company envelope  \",\"TaxNumber\":\"123000\",\"Registrar\":\"Office\",\"Id\":999,\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\",\"Customer\":[{\"$id\":\"C\",\"Id\":998,\"FirstName\":\"NeverPersist\",\"LastName\":\"Graph\",\"FullName\":\"forged full\",\"Telephone\":\"T\",\"Mobile\":\"M\",\"Fax\":\"F\",\"Email\":\"nested@example.test\",\"DateOfBirth\":\"2000-01-01\",\"CompanyId\":999,\"BillingAddressId\":997,\"ShippingAddressId\":997,\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\",\"Company\":{\"$ref\":\"K\"},\"BillingAddress\":{\"$id\":\"A\",\"Id\":997,\"Building\":\"Nested\",\"AddressLine1\":\"forged road\",\"AddressLine2\":\"Line2\",\"City\":\"Bangkok\",\"State\":\"State\",\"PostalCode\":\"10100\",\"CountryId\":764,\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\",\"CustomerBillingAddress\":[{\"$ref\":\"C\"}],\"CustomerShippingAddress\":[{\"$ref\":\"C\"}]},\"ShippingAddress\":{\"$ref\":\"A\"}}]}";

    internal static string?[] Values(object? model) => model switch
    {
        SourceCompany value => [value.Name, value.TaxNumber, value.Registrar],
        UpsertCompanyRequest value => [value.Name, value.TaxNumber, value.Registrar],
        _ => throw new InvalidOperationException("Unexpected Company projection.")
    };

    internal static async Task<CustomerBodyRead> ReadAsync(IServiceProvider services, MvcOptions options,
        byte[] bytes, bool update = false, string contentType = "application/json")
    {
        var context = Context(services, bytes, update, contentType);
        using var input = context.HttpContext.Request.Body;
        var formatter = options.InputFormatters.FirstOrDefault(item => item.CanRead(context));
        return formatter is null ? new CustomerBodyRead("unsupported", null, [], 0, [], [])
            : await CustomerOriginalInputOracle.ObserveAsync(formatter, context);
    }

    internal static InputFormatterContext Context(IServiceProvider services, byte[] bytes,
        bool update = false, string contentType = "application/json")
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(bytes, writable: false);
        http.Request.ContentLength = bytes.LongLength;
        http.Request.ContentType = contentType;
        http.Request.Method = update ? "PUT" : "POST";
        http.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature(bytes.Length > 0));
        var controller = typeof(CompaniesController);
        var method = controller.GetMethod(update ? nameof(CompaniesController.UpdateCompanyAsync)
            : nameof(CompaniesController.CreateCompanyAsync))!;
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            new ControllerActionDescriptor { ControllerTypeInfo = controller.GetTypeInfo(), MethodInfo = method }),
            "ordinary-company-reader"));
        return new InputFormatterContext(http, "", new ModelStateDictionary(),
            services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(UpsertCompanyRequest)),
            static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
    }

    private sealed record BodyFeature(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
}
