using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer optional contact boundaries", DisableParallelization = true)]
public sealed class CustomerOptionalContactBoundaryCollection;

[Collection("Customer optional contact boundaries")]
public sealed class CustomerOptionalContactMvcBoundaryTests(CustomerDetailAuthorityFixture fixture, ITestOutputHelper output)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string Valid = "{\"FirstName\":\"Contact\",\"LastName\":\"Boundary\",\"Email\":\"contact@example.test\",\"CompanyId\":1,\"BillingAddressId\":1,\"ShippingAddressId\":1";

    [Fact]
    public void DescriptorAndHeaderSelection_PreserveStrictRoutesAndUnknownEndpoints()
    {
        var (_, native, adapter) = Formatters();
        Assert.True(adapter.CanRead(Context(Valid + ",\"Telephone\":true}", "create")));
        Assert.True(adapter.CanRead(Context(Valid + ",\"Telephone\":true}", "update")));
        foreach (var mode in new[] { "versioned", "unknown" })
            Assert.False(adapter.CanRead(Context(Valid + ",\"Telephone\":true}", mode)));
        foreach (var values in new[] { new[] { "" }, new[] { "invalid" }, new[] { Guid.NewGuid().ToString() }, new[] { "a", "b" } })
        {
            var context = Context("{}", "create");
            context.HttpContext.Request.Headers["Idempotency-Key"] = values;
            Assert.False(adapter.CanRead(context));
            Assert.True(native.CanRead(context));
        }
        var wrongVerb = Context("{}", "create");
        wrongVerb.HttpContext.Request.Method = "GET";
        Assert.False(adapter.CanRead(wrongVerb));
        var forgedPath = Context("{}", "versioned");
        forgedPath.HttpContext.Request.Path = "/customers";
        Assert.False(adapter.CanRead(forgedPath));
        var otherType = Context("{}", "create", modelType: typeof(UpsertCompanyRequest));
        Assert.False(adapter.CanRead(otherType));
        var media = Context("{}", "create");
        media.HttpContext.Request.ContentType = "text/plain";
        Assert.False(adapter.CanRead(media));
    }

    [Fact]
    public async Task OrdinaryFields_MatchReferenceAndIgnoreOnlyContactDuplicateNulls()
    {
        var (_, _, adapter) = Formatters();
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "de-DE", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var field in new[] { "Telephone", "Mobile", "Fax" })
                    foreach (var raw in new[] { "true", "false", "42", "1.500", "1E+03", "-0", "9223372036854775808123456789", "\"00123\"", "\" ก้ 😀 \\t \"", "\"\"", "null" })
                    {
                        var body = Valid + $",\"{field}\":{raw},\"{field}\":null}}";
                        var reference = Newtonsoft.Json.JsonConvert.DeserializeObject<ReferenceContact>(body,
                            new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore })!;
                        var result = await adapter.ReadAsync(Context(body, "create"));
                        Assert.False(result.HasError);
                        var model = Assert.IsType<UpsertCustomerRequest>(result.Model);
                        Assert.Equal(reference.Telephone, model.Telephone);
                        Assert.Equal(reference.Mobile, model.Mobile);
                        Assert.Equal(reference.Fax, model.Fax);
                    }
            }
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        foreach (var raw in new[] { "{}", "[]" })
        {
            var result = await adapter.ReadAsync(Context(Valid + ",\"Telephone\":" + raw + "}", "create"));
            Assert.True(result.HasError);
        }
        var untouched = await adapter.ReadAsync(Context(Valid + ",\"FirstName\":null,\"Telephone\":true}", "create"));
        Assert.Equal("Contact", Assert.IsType<UpsertCustomerRequest>(untouched.Model).FirstName);
        var combined = await adapter.ReadAsync(Context(Valid + ",\"Telephone\":null,\"Telephone\":true,\"Telephone\":null,\"Mobile\":false,\"Mobile\":null,\"Fax\":\"00123\",\"Fax\":null}", "create"));
        var contacts = Assert.IsType<UpsertCustomerRequest>(combined.Model);
        Assert.Equal("true", contacts.Telephone);
        Assert.Equal("false", contacts.Mobile);
        Assert.Equal("00123", contacts.Fax);
        foreach (var resetBody in new[] { Valid + "}", Valid + ",\"Telephone\":null,\"Mobile\":null,\"Fax\":null}" })
        {
            var reset = await adapter.ReadAsync(Context(resetBody, "create"));
            var model = Assert.IsType<UpsertCustomerRequest>(reset.Model);
            Assert.Null(model.Telephone);
            Assert.Null(model.Mobile);
            Assert.Null(model.Fax);
        }
    }

    [Fact]
    public void CustomPoliciesConvertersResolversFactories_FallThroughWithoutMutation()
    {
        AssertVersioningRegistrationOwnership();
        var factory = new ThrowingFactory();
        var (_, stockSelection, ownedSelection) = Formatters(after: options => options.JsonSerializerOptions.Converters.Add(factory));
        Assert.True(stockSelection.CanRead(Context("{}", "create")));
        Assert.False(ownedSelection.CanRead(Context("{}", "create")));
        Assert.Equal(0, factory.Calls);
        foreach (var configure in new Action<JsonOptions>[]
        {
            options => options.JsonSerializerOptions.Converters.Add(new CustomString()),
            options => options.JsonSerializerOptions.Converters.Add(new CustomRequest()),
            options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            options => options.JsonSerializerOptions.PropertyNameCaseInsensitive = false,
            options => options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.Preserve,
            options => options.JsonSerializerOptions.RespectRequiredConstructorParameters = true,
            options => options.JsonSerializerOptions.RespectNullableAnnotations = true,
            options => options.JsonSerializerOptions.PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            options => options.JsonSerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        })
        {
            var (source, _, adapter) = Formatters(after: configure);
            var resolver = source.JsonSerializerOptions.TypeInfoResolver;
            var converters = source.JsonSerializerOptions.Converters.ToArray();
            Assert.False(adapter.CanRead(Context("{}", "create")));
            Assert.Same(resolver, source.JsonSerializerOptions.TypeInfoResolver);
            Assert.Equal(converters, source.JsonSerializerOptions.Converters);
        }
        var (_, _, customFactory) = Formatters(before: options =>
        {
            var resolver = Assert.IsType<DefaultJsonTypeInfoResolver>(options.JsonSerializerOptions.TypeInfoResolver);
            resolver.Modifiers.Add(info => { if (info.Type == typeof(UpsertCustomerRequest)) info.CreateObject = () => new UpsertCustomerRequest("custom", "factory", null, null, null, "factory@example.test", null, null, null, null); });
        });
        Assert.False(customFactory.CanRead(Context("{}", "create")));
    }

    [Fact]
    public async Task DifferentialBodies_KeepEncodingEmptyMalformedAndPrivacyBehavior()
    {
        foreach (var allowMessages in new[] { false, true })
            foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
                foreach (var item in new[] { "", " ", "null", "{", "[]", Valid + "}", Valid + ",\"Telephone\":{}}", Valid + ",\"Telephone\":[]}", Valid + ",\"CompanyId\":\"bad\"}" }
                    .Select((body, index) => (Body: body, CaseIndex: index)))
                {
                    var body = item.Body;
                    var (_, native, adapter) = Formatters(after: options => options.AllowInputFormatterExceptionMessages = allowMessages);
                    var expectedContext = Context(body, "create", encoding);
                    var actualContext = Context(body, "create", encoding);
                    var expected = await native.ReadAsync(expectedContext);
                    var actual = await adapter.ReadAsync(actualContext);
                    var original = await global::Legacy.Maliev.CustomerService.Tests.OriginalInput.CustomerOriginalInputOracle
                        .ReadBytesAsync(global::Legacy.Maliev.CustomerService.Tests.OriginalInput.CustomerOriginalInputOracle.Formatter(),
                            global::Legacy.Maliev.CustomerService.Tests.OriginalInput.CustomerOriginalInputOracle.SourceType("customer"),
                            new EmptyModelMetadataProvider(), encoding.GetBytes(body), expectedContext.HttpContext.Request.ContentType!);
                    Assert.Equal(original.Kind == "failure", actual.HasError);
                    Assert.Equal(original.Kind == "success", actual.IsModelSet);
                    Assert.Equal(expected.HasError, expectedContext.ModelState.ErrorCount > 0);
                    // Owned ordinary input now follows the complete source reader.
                    // Native STJ diagnostics remain a separate unchanged baseline.
                    if (actual.HasError)
                    {
                        var error = Assert.Single(actualContext.ModelState.Values.SelectMany(value => value.Errors));
                        Assert.Equal("The request body contains an invalid value.", error.ErrorMessage);
                        Assert.Null(error.Exception);
                    }
                    else Assert.Empty(Errors(actualContext));
                    Assert.True(actualContext.HttpContext.Request.Body.CanRead);
                }
        foreach (var emptyDefault in new[] { false, true })
        {
            var (_, native, adapter) = Formatters();
            var expected = await native.ReadAsync(Context("", "create", canHaveBody: false, emptyDefault: emptyDefault));
            var actual = await adapter.ReadAsync(Context("", "create", canHaveBody: false, emptyDefault: emptyDefault));
            Assert.Equal(expected.IsModelSet, actual.IsModelSet);
            Assert.Equal(expected.HasError, actual.HasError);
        }
        var (_, builtin, candidate) = Formatters();
        var a = Context("{}", "create"); a.HttpContext.Request.ContentType = "application/json; charset=iso-8859-1";
        var b = Context("{}", "create"); b.HttpContext.Request.ContentType = a.HttpContext.Request.ContentType;
        Assert.Equal((await builtin.ReadAsync(a)).HasError, (await candidate.ReadAsync(b)).HasError);
        Assert.Contains(b.ModelState.Values.SelectMany(value => value.Errors),
            error => error.Exception is UnsupportedContentTypeException);
    }

    [Fact]
    public async Task DifferentialExceptionalConverters_KeepFormatOverflowAndCancellation()
    {
        foreach (var overflow in new[] { false, true })
        {
            var (_, native, adapter) = Formatters(after: options => options.JsonSerializerOptions.Converters.Add(new ThrowingDate(overflow)));
            var a = Context(Valid + ",\"DateOfBirth\":\"x\"}", "create");
            var b = Context(Valid + ",\"DateOfBirth\":\"x\"}", "create");
            Assert.Equal((await native.ReadAsync(a)).HasError, (await adapter.ReadAsync(b)).HasError);
            Assert.Equal(Errors(a), Errors(b));
        }
        var (_, builtin, candidate) = Formatters();
        var expected = Context("{}", "create"); expected.HttpContext.Request.Body = new CanceledStream();
        var actual = Context("{}", "create"); actual.HttpContext.Request.Body = new CanceledStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builtin.ReadAsync(expected));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => candidate.ReadAsync(actual));
    }

    [Fact]
    public async Task ConcurrentReads_IsolateOrdinaryVersionedReplayAndDefaultSerialization()
    {
        var (_, native, adapter) = Formatters();
        var body = Valid + ",\"Telephone\":true}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new ReadGate(24, timeout.Token);
        var tasks = Enumerable.Range(0, 24).Select(async index =>
        {
            var mode = index % 3 == 1 ? "versioned" : "create";
            var context = Context(body, mode);
            if (index % 3 == 2) context.HttpContext.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString();
            using var stream = new CoordinatedStream(Encoding.UTF8.GetBytes(body), gate);
            context.HttpContext.Request.Body = stream;
            var eligible = adapter.CanRead(context);
            Assert.Equal(index % 3 == 0, eligible);
            var result = await (eligible ? adapter : native).ReadAsync(context);
            Assert.Equal(index % 3 != 0, result.HasError);
            if (eligible)
            {
                Assert.Equal("true", Assert.IsType<UpsertCustomerRequest>(result.Model).Telephone);
                var resetContext = Context(Valid + ",\"Telephone\":null,\"Mobile\":null,\"Fax\":null}", "create");
                using var resetBody = resetContext.HttpContext.Request.Body;
                var reset = await adapter.ReadAsync(resetContext);
                var model = Assert.IsType<UpsertCustomerRequest>(reset.Model);
                Assert.Null(model.Telephone);
                Assert.Null(model.Mobile);
                Assert.Null(model.Fax);
            }
        }).ToArray();
        ExceptionDispatchInfo? primaryFailure = null;
        Exception? cleanupFailure = null;
        try { await gate.AllEntered.Task.WaitAsync(timeout.Token); }
        catch (Exception failure) { primaryFailure = ExceptionDispatchInfo.Capture(failure); }
        finally
        {
            gate.Release.TrySetResult(true);
            // Cleanup must not inherit an already-cancelled admission token.
            var settled = Task.WhenAll(tasks);
            try
            {
                try { await settled.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException)
                {
                    // Every gate has its own finite token. Observe the original tasks even
                    // when the independent cleanup budget fails, then retain that failure.
                    try { await settled; }
                    catch (Exception failure) { cleanupFailure = settled.Exception ?? failure; }
                    throw;
                }
            }
            catch (Exception failure) { cleanupFailure ??= settled.Exception ?? failure; }
        }
        if (primaryFailure is not null)
        {
            if (cleanupFailure is not null)
                primaryFailure.SourceException.Data["ObservedCleanupFailure"] = cleanupFailure.ToString();
            primaryFailure.Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertCustomerRequest>(body));
        var record = new UpsertCustomerRequest("A", "B", "001", null, null, "a@b.test", null, null, null, null);
        var before = JsonSerializer.Serialize(record);
        await adapter.ReadAsync(Context(body, "create"));
        Assert.Equal(before, JsonSerializer.Serialize(record));
    }

    [Fact]
    public async Task RealHttp_OrdinaryCreateAndUpdatePreserveLiteralContactsAndState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        foreach (var update in new[] { false, true })
        {
            await fixture.ResetAsync();
            await using var host = fixture.Start();
            using var client = fixture.Client(host, "customer-lifecycle");
            using var request = new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Post, update ? "/customers/1" : "/customers")
            { Content = new StringContent(Valid + ",\"Telephone\":true,\"Telephone\":null,\"Mobile\":1E+03,\"Fax\":\"00123\"}", Encoding.UTF8, "application/json") };
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.StatusCode != (update ? HttpStatusCode.NoContent : HttpStatusCode.Created))
            {
                WriteFailureDiagnostic("public-host-options-constructed-probe", () => DescribeHost(host, update));
                await WriteResponseShapeAsync(response, update, timeout.Token);
            }
            Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
            var id = 1;
            if (!update)
            {
                using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                id = created.RootElement.GetProperty("Id").GetInt32();
            }
            using var read = await client.GetAsync($"/customers/{id}", timeout.Token);
            using var wire = JsonDocument.Parse(await read.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("true", wire.RootElement.GetProperty("Telephone").GetString());
            Assert.Equal("1E+03", wire.RootElement.GetProperty("Mobile").GetString());
            Assert.Equal("00123", wire.RootElement.GetProperty("Fax").GetString());
            Assert.False(wire.RootElement.TryGetProperty("telephone", out _));
        }
    }

    [Fact]
    public async Task RealHttp_ReplayVersionedAndDeniedWritesStayStrictAndUnchanged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        // ClientHandler.Append removes a single empty value; inject a present empty key after that copy.
        using var emptyHeaderClient = new HttpClient(host.Server.CreateHandler(context =>
        {
            Assert.False(context.Request.Headers.ContainsKey("Idempotency-Key"));
            context.Request.Headers["Idempotency-Key"] = new Microsoft.Extensions.Primitives.StringValues(string.Empty);
            Assert.True(context.Request.Headers.ContainsKey("Idempotency-Key"));
            Assert.Equal(1, context.Request.Headers["Idempotency-Key"].Count);
            Assert.Equal(string.Empty, context.Request.Headers["Idempotency-Key"][0]);
        }))
        { BaseAddress = client.BaseAddress };
        emptyHeaderClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        var before = await fixture.SnapshotAsync();
        foreach (var target in new[] { "versioned", "replay-empty", "replay-multiple", "replay-valid" })
        {
            using var request = new HttpRequestMessage(target == "versioned" ? HttpMethod.Put : HttpMethod.Post,
                target == "versioned" ? "/customers/1/versioned" : "/customers")
            { Content = new StringContent(Valid + ",\"Telephone\":true}", Encoding.UTF8, "application/json") };
            if (target != "versioned") request.Headers.TryAddWithoutValidation("Idempotency-Key",
                target == "replay-empty" ? new[] { "" } : target == "replay-multiple" ? new[] { "a", "b" } : new[] { Guid.NewGuid().ToString() });
            using var response = await (target == "replay-empty" ? emptyHeaderClient : client).SendAsync(request, timeout.Token);
            if (response.StatusCode != HttpStatusCode.BadRequest)
                WriteFailureDiagnostic("strict-route-status", () => new { Case = target, ExpectedStatus = 400, ActualStatus = (int)response.StatusCode });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await fixture.SnapshotAsync());
        }
        using var anonymous = fixture.Client(host, "anonymous");
        using var denied = fixture.Client(host, "denied");
        foreach (var restricted in new[] { anonymous, denied })
        {
            using var response = await restricted.PostAsync("/customers", new StringContent(Valid + ",\"Telephone\":true}", Encoding.UTF8, "application/json"), timeout.Token);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            Assert.Equal(before, await fixture.SnapshotAsync());
        }
    }

    [Fact]
    public async Task FormattersAndGlobalOptions_RemainUnmodifiedAcrossReads()
    {
        AssertVersioningMemberBehavior();
        var (source, native, adapter) = Formatters();
        var originalResolver = source.JsonSerializerOptions.TypeInfoResolver;
        var originalConverters = source.JsonSerializerOptions.Converters.ToArray();
        var nativeOptions = native.SerializerOptions;
        await adapter.ReadAsync(Context(Valid + ",\"Telephone\":true}", "create"));
        Assert.Same(nativeOptions, native.SerializerOptions);
        Assert.Same(originalResolver, source.JsonSerializerOptions.TypeInfoResolver);
        Assert.Equal(originalConverters, source.JsonSerializerOptions.Converters);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertCustomerRequest>(Valid + ",\"Telephone\":true}", source.JsonSerializerOptions));
        AssertPrivateContactConvertersPreserveConstructorAndWrites(source);
    }

    [Fact]
    public async Task StreamAndPipeSwitch_DifferentialReaderLeavesRequestBodyOpen()
    {
        // Exercise the actual process setting without mutating global AppContext state.
        // The opposite branch requires a separately admitted process configured before startup.
        var existed = AppContext.TryGetSwitch("Microsoft.AspNetCore.UseStreamBasedJsonParsing", out var previous);
        var (_, native, adapter) = Formatters();
        foreach (var contentType in new[] { "application/json", "text/json", "application/problem+json" })
        {
            var a = Context(Valid + "}", "create"); a.HttpContext.Request.ContentType = contentType;
            var b = Context(Valid + "}", "create"); b.HttpContext.Request.ContentType = contentType;
            Assert.Equal(native.CanRead(a), adapter.CanRead(b));
            var expected = await native.ReadAsync(a);
            var actual = await adapter.ReadAsync(b);
            Assert.Equal(expected.Model, actual.Model);
            Assert.True(b.HttpContext.Request.Body.CanRead);
        }
        Assert.Equal(existed, AppContext.TryGetSwitch("Microsoft.AspNetCore.UseStreamBasedJsonParsing", out var current));
        Assert.Equal(previous, current);
    }

    private static (JsonOptions Source, SystemTextJsonInputFormatter Native, TextInputFormatter Adapter) Formatters(
        Action<JsonOptions>? before = null, Action<JsonOptions>? after = null)
    {
        var source = new JsonOptions();
        source.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        source.JsonSerializerOptions.PropertyNamingPolicy = null;
        source.JsonSerializerOptions.DictionaryKeyPolicy = null;
        before?.Invoke(source);
        var type = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter", throwOnError: true)!;
        type.GetMethod("ComposeOwned", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source.JsonSerializerOptions]);
        after?.Invoke(source);
        var native = new SystemTextJsonInputFormatter(source, NullLogger<SystemTextJsonInputFormatter>.Instance);
        var adapter = (TextInputFormatter)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [source, native, NullLogger<SystemTextJsonInputFormatter>.Instance], culture: null)!;
        return (source, native, adapter);
    }

    private static void AssertVersioningRegistrationOwnership()
    {
        var formatterType = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter", true)!;
        var shape = formatterType.GetNestedType("VersioningOwnershipProfile", BindingFlags.NonPublic)!
            .GetMethod("SameShape", BindingFlags.Static | BindingFlags.NonPublic)!;
        var firstTarget = new FactoryProbe();
        var otherTarget = new FactoryProbe();
        var expectedFactory = ServiceDescriptor.Singleton(typeof(BoundaryAnnotation), firstTarget.Create);
        Assert.True((bool)shape.Invoke(null, [ServiceDescriptor.Singleton(typeof(BoundaryAnnotation), firstTarget.Create), expectedFactory])!);
        Assert.False((bool)shape.Invoke(null, [ServiceDescriptor.Singleton(typeof(BoundaryAnnotation), otherTarget.Create), expectedFactory])!);
        Assert.Equal(expectedFactory.ImplementationFactory!.Method, ((Func<IServiceProvider, object>)otherTarget.Create).Method);
        Assert.Equal(0, firstTarget.Calls);
        Assert.Equal(0, otherTarget.Calls);
        using (var positive = VersioningFormatters())
        {
            Assert.True(positive.Services.IsReadOnly);
            Assert.True(positive.Adapter.CanRead(Context("{}", "create")));
            Assert.Equal(ServiceLifetime.Transient, positive.Services.First(item => item.ServiceType == typeof(IPostConfigureOptions<JsonOptions>)).Lifetime);
            Assert.Same(positive.Source.JsonSerializerOptions, positive.Native.SerializerOptions);
            Assert.Equal(new Guid("ae14e30a-df28-4e58-9f5a-1254d1b7817b"), typeof(Asp.Versioning.MvcApiVersioningOptions).Module.ModuleVersionId);
            Assert.Equal(new Guid("e23c6dbb-ef2c-42b8-8bab-c8e6a254ec5c"), typeof(Asp.Versioning.ApiVersioningFeature).Module.ModuleVersionId);
        }
        using (var repeatedDescriptor = VersioningFormatters())
        {
            Assert.True(repeatedDescriptor.Adapter.CanRead(Context("{}", "create")));
            var callbacks = repeatedDescriptor.Provider.GetServices<IPostConfigureOptions<JsonOptions>>().ToArray();
            Assert.Equal(2, callbacks.Length);
            // Transient resolution creates a new wrapper from the same tracked descriptor.
            // Reprocessing these already-adopted options must revoke ownership.
            callbacks[0].PostConfigure(Options.DefaultName, repeatedDescriptor.Source);
            Assert.False(repeatedDescriptor.Adapter.CanRead(Context("{}", "create")));
        }
        using (var originalCallbackFault = VersioningFormatters())
        {
            Assert.True(originalCallbackFault.Adapter.CanRead(Context("{}", "create")));
            var callbacks = originalCallbackFault.Provider.GetServices<IPostConfigureOptions<JsonOptions>>().ToArray();
            Assert.Equal(2, callbacks.Length);
            var resolver = originalCallbackFault.Source.JsonSerializerOptions.TypeInfoResolver;
            originalCallbackFault.Source.JsonSerializerOptions.MakeReadOnly();
            // The actual vendor callback faults on its normal resolver assignment.
            // This is a repeated-processing fault, not a first-adoption fault.
            Assert.Throws<InvalidOperationException>(() => callbacks[0].PostConfigure(Options.DefaultName, originalCallbackFault.Source));
            Assert.Same(resolver, originalCallbackFault.Source.JsonSerializerOptions.TypeInfoResolver);
            Assert.False(originalCallbackFault.Adapter.CanRead(Context("{}", "create")));
        }
        using (var mutable = VersioningFormatters(freeze: false))
            Assert.False(mutable.Adapter.CanRead(Context("{}", "create")));
        foreach (var after in new[] { false, true })
            foreach (var mutation in new Action<IServiceCollection>[]
            {
            services => services.Configure<JsonOptions>(_ => { }),
            services => services.PostConfigure<JsonOptions>(_ => { }),
            services => services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor()),
            services => services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>(),
            services => services.AddKeyedSingleton<IHttpContextAccessor, HttpContextAccessor>("other"),
            services => services.AddKeyedSingleton<IPostConfigureOptions<JsonOptions>>("other", new EmptyPostConfigure()),
            services => services.AddSingleton<Asp.Versioning.IAnnotation<MemberInfo, Asp.Versioning.ApiVersionRange>>(new BoundaryAnnotation()),
            services =>
            {
                var descriptor = services.Single(item => item.ServiceType == typeof(Asp.Versioning.IAnnotation<MemberInfo, Asp.Versioning.ApiVersionRange>));
                services.Remove(descriptor);
                // A forwarding delegate must not be treated as the canonical vendor factory.
                var factory = descriptor.ImplementationFactory!;
                services.Add(ServiceDescriptor.Singleton(descriptor.ServiceType, provider => factory(provider)));
            },
            services =>
            {
                var descriptor = services.First(item => item.ServiceType == typeof(IPostConfigureOptions<JsonOptions>));
                services.Add(descriptor);
            },
            services =>
            {
                var descriptor = services.Single(item => item.ServiceType == typeof(Asp.Versioning.IAnnotation<MemberInfo, Asp.Versioning.ApiVersionRange>));
                services.Remove(descriptor);
                var factory = descriptor.ImplementationFactory!;
                var multicast = (Func<IServiceProvider, object>)Delegate.Combine(factory, factory);
                services.Add(ServiceDescriptor.Singleton(descriptor.ServiceType, multicast));
            },
            services =>
            {
                var descriptor = services.Single(item => item.ServiceType == typeof(IHttpContextAccessor));
                services.Remove(descriptor);
                services.Add(descriptor);
            },
            })
            {
                using var candidate = VersioningFormatters(beforeTrack: after ? null : mutation, afterTrack: after ? mutation : null);
                Assert.False(candidate.Adapter.CanRead(Context("{}", "create")));
            }
        var calls = 0;
        string? observedName = null;
        JsonOptions? observedOptions = null;
        using (var unsupportedFactory = VersioningFormatters(beforeTrack: services =>
        {
            var descriptor = services.Single(item => item.ServiceType == typeof(IPostConfigureOptions<JsonOptions>));
            var position = services.IndexOf(descriptor);
            services[position] = ServiceDescriptor.Transient<IPostConfigureOptions<JsonOptions>>(provider =>
                new ObservedPostConfigure((IPostConfigureOptions<JsonOptions>)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!),
                    (name, options) => { calls++; observedName = name; observedOptions = options; }));
        }))
        {
            Assert.Equal(1, calls);
            Assert.Equal(Options.DefaultName, observedName);
            Assert.Same(unsupportedFactory.Source, observedOptions);
            Assert.False(unsupportedFactory.Adapter.CanRead(Context("{}", "create")));
        }
        using (var repeated = VersioningFormatters())
        {
            var type = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter", true)!;
            type.GetMethod("ComposeOwned", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [repeated.Source.JsonSerializerOptions]);
            Assert.False(repeated.Adapter.CanRead(Context("{}", "create")));
        }
        var thrown = new InvalidOperationException("fixed synthetic callback failure");
        var throwCalls = 0;
        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() =>
        {
            using var _ = VersioningFormatters(afterTrack: services => services.PostConfigure<JsonOptions>(options =>
            {
                throwCalls++;
                options.JsonSerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
                throw thrown;
            }));
        }));
        Assert.Equal(1, throwCalls);
    }

    private static void AssertVersioningMemberBehavior()
    {
        using var configured = VersioningFormatters();
        var accessor = configured.Provider.GetRequiredService<IHttpContextAccessor>();
        Assert.IsType<HttpContextAccessor>(accessor);
        var annotations = configured.Provider.GetRequiredService<Asp.Versioning.IAnnotation<MemberInfo, Asp.Versioning.ApiVersionRange>>();
        Assert.Equal("Asp.Versioning.AnnotationCache", annotations.GetType().FullName);
        Assert.Same(annotations, configured.Provider.GetRequiredService<Asp.Versioning.IAnnotation<MemberInfo, Asp.Versioning.ApiVersionRange>>());
        try
        {
            foreach (var major in new int?[] { null, 1, 2 })
            {
                var http = new DefaultHttpContext { RequestServices = configured.Provider };
                http.Features.Set<Asp.Versioning.IApiVersioningFeature>(new Asp.Versioning.ApiVersioningFeature(http)
                {
                    RequestedApiVersion = major is { } value ? new Asp.Versioning.ApiVersion(value, 0) : null,
                });
                accessor.HttpContext = http;
                var options = new JsonSerializerOptions(configured.Source.JsonSerializerOptions);
                var body = JsonSerializer.Serialize(new VersionedContactBoundary { Annotated = "visible", Ordinary = "always" }, options);
                using var json = JsonDocument.Parse(body);
                Assert.Equal(major != 1, json.RootElement.TryGetProperty("Annotated", out _));
                Assert.Equal("always", json.RootElement.GetProperty("Ordinary").GetString());
                const string annotated = "{\"Annotated\":\"received\",\"Ordinary\":\"always\"}";
                if (major == 1)
                    Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VersionedContactBoundary>(annotated, options));
                else
                    Assert.Equal("received", JsonSerializer.Deserialize<VersionedContactBoundary>(annotated, options)!.Annotated);
                var ordinary = JsonSerializer.Deserialize<VersionedContactBoundary>("{\"Ordinary\":\"always\"}", options)!;
                Assert.Null(ordinary.Annotated);
                Assert.Equal("always", ordinary.Ordinary);
            }
        }
        finally { accessor.HttpContext = null; }
    }

    private static VersioningBoundary VersioningFormatters(Action<IServiceCollection>? beforeTrack = null,
        Action<IServiceCollection>? afterTrack = null, bool freeze = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiVersioning(options => options.ApiVersionReader = new Asp.Versioning.QueryStringApiVersionReader()).AddMvc();
        services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
            options.JsonSerializerOptions.DictionaryKeyPolicy = null;
            var formatterType = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter", true)!;
            formatterType.GetMethod("ComposeOwned", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [options.JsonSerializerOptions]);
        });
        var own = services.Last(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<JsonOptions>));
        beforeTrack?.Invoke(services);
        var type = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter", true)!;
        var before = services.ToArray();
        type.GetMethod("TrackVersioningPostConfigure", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [services, own]);
        Assert.All(before.Where(descriptor => descriptor.ServiceType != typeof(IPostConfigureOptions<JsonOptions>)), descriptor => Assert.Contains(descriptor, services));
        afterTrack?.Invoke(services);
        if (freeze) services.MakeReadOnly();
        var provider = services.BuildServiceProvider();
        try
        {
            var source = provider.GetRequiredService<IOptions<JsonOptions>>().Value;
            var native = new SystemTextJsonInputFormatter(source, NullLogger<SystemTextJsonInputFormatter>.Instance);
            var adapter = (TextInputFormatter)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [source, native, NullLogger<SystemTextJsonInputFormatter>.Instance], culture: null)!;
            return new VersioningBoundary(services, provider, source, native, adapter);
        }
        catch { provider.Dispose(); throw; }
    }

    private sealed record VersioningBoundary(ServiceCollection Services, ServiceProvider Provider, JsonOptions Source,
        SystemTextJsonInputFormatter Native, TextInputFormatter Adapter) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private sealed class ObservedPostConfigure(IPostConfigureOptions<JsonOptions> inner, Action<string?, JsonOptions> observe) : IPostConfigureOptions<JsonOptions>
    {
        public void PostConfigure(string? name, JsonOptions options) { observe(name, options); inner.PostConfigure(name, options); }
    }

    private sealed class EmptyPostConfigure : IPostConfigureOptions<JsonOptions>
    {
        public void PostConfigure(string? name, JsonOptions options) { }
    }

    private sealed class BoundaryAnnotation : Asp.Versioning.IAnnotation<MemberInfo, Asp.Versioning.ApiVersionRange>
    {
        public bool TryGet(MemberInfo key, out Asp.Versioning.ApiVersionRange value) { value = Asp.Versioning.ApiVersionRange.Empty; return false; }
    }

    private sealed class FactoryProbe
    {
        public int Calls { get; private set; }
        public object Create(IServiceProvider provider) { Calls++; return new BoundaryAnnotation(); }
    }

    private sealed class VersionedContactBoundary
    {
        [Asp.Versioning.VisibleInApiVersion("[2.0,)")]
        public string? Annotated { get; set; }
        public string? Ordinary { get; set; }
    }

    private static InputFormatterContext Context(string body, string action, Encoding? encoding = null,
        bool canHaveBody = true, bool emptyDefault = false, Type? modelType = null)
    {
        encoding ??= Encoding.UTF8;
        var http = new DefaultHttpContext();
        http.Request.Method = action == "create" ? "POST" : "PUT";
        http.Request.ContentType = "application/json; charset=" + encoding.WebName;
        http.Request.Body = new MemoryStream(encoding.GetBytes(body));
        http.Request.ContentLength = encoding.GetByteCount(body);
        http.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature(canHaveBody));
        var method = action == "create" ? nameof(CustomersController.CreateCustomerAsync)
            : action == "versioned" ? nameof(CustomersController.UpdateCustomerVersionedAsync) : nameof(CustomersController.UpdateCustomerAsync);
        if (action != "unknown")
        {
            var descriptor = new ControllerActionDescriptor
            {
                ControllerTypeInfo = typeof(CustomersController).GetTypeInfo(),
                MethodInfo = typeof(CustomersController).GetMethod(method)!
            };
            http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(descriptor), "boundary"));
        }
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(modelType ?? typeof(UpsertCustomerRequest));
        return new InputFormatterContext(http, string.Empty, new ModelStateDictionary(), metadata,
            (stream, selectedEncoding) => new StreamReader(stream, selectedEncoding), emptyDefault);
    }

    private static void AssertPrivateContactConvertersPreserveConstructorAndWrites(JsonOptions source)
    {
        // The private stateful STJ converters were superseded by the complete
        // source-object reader. Preserve their observable isolation/write cases.
        var (_, _, adapter) = Formatters();
        var bodies = new[]
        {
            (Json: "\"Telephone\":null,\"Mobile\":null,\"Fax\":null", Telephone: (string?)null, Mobile: (string?)null, Fax: (string?)null),
            (Json: "\"Telephone\":true,\"Telephone\":null,\"Mobile\":false,\"Mobile\":null,\"Fax\":\"00123\",\"Fax\":null", Telephone: "true", Mobile: "false", Fax: "00123"),
            (Json: "\"Telephone\":\"\",\"Telephone\":null", Telephone: "", Mobile: (string?)null, Fax: (string?)null),
        };
        var baseline = JsonSerializer.Serialize(new UpsertCustomerRequest(
            "A", "B", "written-only", null, null, "a@b.test", null, null, null, null), source.JsonSerializerOptions);
        foreach (var body in bodies)
        {
            var context = Context(Valid + "," + body.Json + "}", "create");
            using var stream = context.HttpContext.Request.Body;
            var result = adapter.ReadAsync(context).GetAwaiter().GetResult();
            Assert.False(result.HasError);
            var value = Assert.IsType<UpsertCustomerRequest>(result.Model);
            Assert.Equal(body.Telephone, value.Telephone);
            Assert.Equal(body.Mobile, value.Mobile);
            Assert.Equal(body.Fax, value.Fax);
            foreach (var field in new[] { "Telephone", "Mobile", "Fax" })
                Assert.Null(source.JsonSerializerOptions.GetTypeInfo(typeof(UpsertCustomerRequest)).Properties
                    .Single(property => property.Name == field).CustomConverter);
            Assert.Equal(baseline, JsonSerializer.Serialize(new UpsertCustomerRequest(
                "A", "B", "written-only", null, null, "a@b.test", null, null, null, null), source.JsonSerializerOptions));
        }
        foreach (var rejected in new[] { "{}", "[]" })
        {
            var context = Context(Valid + ",\"Telephone\":" + rejected + "}", "create");
            using var stream = context.HttpContext.Request.Body;
            Assert.True(adapter.ReadAsync(context).GetAwaiter().GetResult().HasError);
            var reset = Context(Valid + ",\"Telephone\":null}", "create");
            using var resetStream = reset.HttpContext.Request.Body;
            Assert.Null(Assert.IsType<UpsertCustomerRequest>(adapter.ReadAsync(reset).GetAwaiter().GetResult().Model).Telephone);
        }
    }

    private static string? ReadContactToken(JsonConverter<string> converter, string json, JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        Assert.True(reader.Read());
        return converter.Read(ref reader, typeof(string), options);
    }

    private static string WriteContactToken(JsonConverter<string> converter, string value, JsonSerializerOptions options)
    {
        using var bytes = new MemoryStream();
        using var writer = new Utf8JsonWriter(bytes);
        converter.Write(writer, value, options);
        writer.Flush();
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private void WriteFailureDiagnostic(string label, Func<object> describe)
    {
        try { output.WriteLine("ContactBoundaryDiagnostic " + label + ": " + JsonSerializer.Serialize(describe())); }
        catch (Exception failure)
        {
            // Diagnostic failures must not replace the original assertion that follows.
            try { output.WriteLine("ContactBoundaryDiagnostic unavailable: " + failure.GetType().FullName); }
            catch (Exception) { }
        }
    }

    private static object DescribeHost(WebApplicationFactory<Program> host, bool update)
    {
        var mvc = host.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var source = host.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var stock = mvc.InputFormatters.OfType<SystemTextJsonInputFormatter>()
            .SingleOrDefault(formatter => formatter.GetType() == typeof(SystemTextJsonInputFormatter));
        var adapter = mvc.InputFormatters.OfType<TextInputFormatter>().SingleOrDefault(formatter =>
            formatter.GetType().Assembly == typeof(Program).Assembly
            && formatter.GetType().FullName == "Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter");
        var method = update ? nameof(CustomersController.UpdateCustomerAsync) : nameof(CustomersController.CreateCustomerAsync);
        var descriptor = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().Single(action => action.ControllerTypeInfo.AsType() == typeof(CustomersController)
                && action.MethodInfo.Name == method);
        var probe = Context("{}", update ? "update" : "create");
        using var probeBody = probe.HttpContext.Request.Body;
        probe.HttpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(descriptor), "diagnostic-probe"));
        return new
        {
            FormatterOrder = mvc.InputFormatters.Select((formatter, index) => new { Index = index, Type = formatter.GetType().FullName }).ToArray(),
            AdapterPresent = adapter is not null,
            SourceStockOptionsSameReference = stock is not null && ReferenceEquals(source, stock.SerializerOptions),
            ResolverType = source.TypeInfoResolver?.GetType().FullName,
            ConverterTypes = source.Converters.Select(converter => converter.GetType().FullName).ToArray(),
            IgnoreCondition = source.DefaultIgnoreCondition.ToString(),
            source.PropertyNameCaseInsensitive,
            NamingPolicyType = source.PropertyNamingPolicy?.GetType().FullName,
            DictionaryNamingPolicyType = source.DictionaryKeyPolicy?.GetType().FullName,
            ReferenceHandlerType = source.ReferenceHandler?.GetType().FullName,
            source.RespectRequiredConstructorParameters,
            source.RespectNullableAnnotations,
            CreationHandling = source.PreferredObjectCreationHandling.ToString(),
            source.IgnoreReadOnlyProperties,
            ConstructedProbe = new
            {
                ActualRequestEvidence = false,
                BodyRead = false,
                DescriptorMethod = descriptor.MethodInfo.Name,
                Eligible = adapter?.CanRead(probe),
            },
        };
    }

    private async Task WriteResponseShapeAsync(HttpResponseMessage response, bool update, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[8193];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length == bytes.Length)
            {
                WriteFailureDiagnostic("response-shape-bounded", () => new { LimitReached = true, Status = (int)response.StatusCode });
                return;
            }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
            var root = document.RootElement;
            string[] fields = ["$", "request", "FirstName", "LastName", "Email", "Telephone", "Mobile", "Fax", "DateOfBirth",
                "CompanyId", "BillingAddressId", "ShippingAddressId", "$.Telephone", "$.Mobile", "$.Fax", "$.CompanyId"];
            WriteFailureDiagnostic("response-structure-only", () => new
            {
                Operation = update ? "ordinary-update" : "ordinary-create",
                Status = (int)response.StatusCode,
                MediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
                {
                    "application/problem+json" => "problem-json",
                    "application/json" => "json",
                    "text/plain" => "text",
                    null => "missing",
                    _ => "other",
                },
                RootKind = root.ValueKind.ToString(),
                // Whitelist structural field names only; never emit response string values or extensions.
                ErrorFields = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Object ? errors.EnumerateObject().Take(16).Select(property => new
                    {
                        Field = fields.Contains(property.Name, StringComparer.Ordinal) ? property.Name : "unrecognized",
                        Kind = property.Value.ValueKind.ToString(),
                        Count = property.Value.ValueKind == JsonValueKind.Array ? property.Value.GetArrayLength() : 0,
                    }).ToArray() : null,
            });
        }
        catch (Exception failure)
        {
            WriteFailureDiagnostic("response-structure-unavailable", () => new { FailureType = failure.GetType().FullName });
        }
    }

    private static string[] Errors(InputFormatterContext context) => context.ModelState.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .SelectMany(pair => pair.Value!.Errors.Select(error => pair.Key + "|" + error.ErrorMessage + "|" + error.Exception?.GetType().FullName)).ToArray();
    private sealed record BodyFeature(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
    private sealed class ReferenceContact { public string? Telephone { get; set; } public string? Mobile { get; set; } public string? Fax { get; set; } }
    private sealed class CustomString : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => "custom";
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
    private sealed class ThrowingFactory : JsonConverterFactory
    {
        internal int Calls { get; private set; }
        public override bool CanConvert(Type typeToConvert)
        {
            Calls++;
            throw new InvalidOperationException("Selection must not invoke custom converter code.");
        }
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            throw new InvalidOperationException("Selection must not create custom converters.");
    }
    private sealed class CustomRequest : JsonConverter<UpsertCustomerRequest>
    {
        public override UpsertCustomerRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new JsonException("custom");
        public override void Write(Utf8JsonWriter writer, UpsertCustomerRequest value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
    private sealed class ThrowingDate(bool overflow) : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw (overflow ? (Exception)new OverflowException("controlled") : new FormatException("controlled"));
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
    private sealed class CanceledStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromCanceled<int>(new CancellationToken(true));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromCanceled<int>(new CancellationToken(true));
    }

    private sealed class ReadGate(int participants, CancellationToken timeout)
    {
        private int remaining = participants;
        internal TaskCompletionSource<bool> AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task EnterAsync()
        {
            if (Interlocked.Decrement(ref remaining) == 0) AllEntered.TrySetResult(true);
            await Release.Task.WaitAsync(timeout);
        }
    }

    private sealed class CoordinatedStream(byte[] bytes, ReadGate gate) : MemoryStream(bytes)
    {
        private int entered;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref entered, 1) == 0) await gate.EnterAsync();
            return await base.ReadAsync(buffer, cancellationToken);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref entered, 1) == 0) await gate.EnterAsync();
            return await base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }
}
