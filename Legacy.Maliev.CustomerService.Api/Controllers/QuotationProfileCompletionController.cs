using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Api.Controllers;

/// <summary>Customer-token-only fill-missing profile boundary; employee and service permissions cannot substitute for ownership.</summary>
[ApiController]
[Authorize]
[Route("customers/{id:int}/instant-quotation-profile-completion")]
public sealed class QuotationProfileCompletionController(CustomerDbContext db, ICustomerCache cache) : ControllerBase
{
    /// <summary>Reads the exact validated customer's graph with a strong, uncached graph revision.</summary>
    [HttpGet]
    public async Task<IActionResult> Read(int id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, private";
        if (!Owner(id, out _) || !TrustedEmail(out _)) return Forbid();
        var store = new QuotationProfileCompletionStore(db);
        if (!await store.IsReadyAsync(cancellationToken)) return StatusCode(503);
        var graph = await store.ReadAsync(id, cancellationToken);
        if (graph is null) return NotFound();
        Response.Headers["X-Quotation-Profile-Completion-Contract"] = "2";
        Response.Headers.ETag = graph.ETag;
        return Ok(graph.Customer);
    }

    /// <summary>Completes empty fields atomically after the consumer has persisted its quotation reference.</summary>
    [HttpPost]
    public async Task<IActionResult> Complete(int id, QuotationProfileCompletionRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, private";
        if (!Owner(id, out var actor) || !TrustedEmail(out var trustedEmail)) return Forbid();
        if (!Request.Headers.TryGetValue("If-Match", out var revision)) return StatusCode(428);
        if (revision.Count != 1 || revision[0] is not { Length: 66 } value || value[0] != '"' || value[^1] != '"' || !value.AsSpan(1, 64).ToString().All(Uri.IsHexDigit)) return BadRequest();
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1 || !Guid.TryParseExact(keys[0], "D", out var key) || key == Guid.Empty) return BadRequest();
        var store = new QuotationProfileCompletionStore(db);
        if (!await store.IsReadyAsync(cancellationToken)) return StatusCode(503);
        var result = await store.CompleteAsync(id, actor!, key, value, request, cancellationToken, trustedEmail);
        if (result.Status != 200) return StatusCode(result.Status);
        // A replay also invalidates: a prior committed write may have outlived a cache failure.
        if (result.Receipt!.Changed) await cache.RemoveAsync(id, cancellationToken);
        return Ok(result.Receipt);
    }

    private bool Owner(int id, out string? actor)
    {
        actor = null;
        var kinds = User.FindAll("identity_kind").ToArray();
        var subjects = User.FindAll("sub").ToArray();
        var ids = User.FindAll("legacy_database_id").ToArray();
        if (kinds.Length != 1 || kinds[0].Value != "customer" || subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value)
            || ids.Length != 1 || !int.TryParse(ids[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var owner) || owner <= 0 || owner != id) return false;
        actor = subjects[0].Value;
        return true;
    }

    private bool TrustedEmail(out string? email)
    {
        email = null;
        var claims = User.FindAll("email").ToArray();
        if (claims.Length == 0) return true;
        if (claims.Length != 1) return false;
        var value = claims[0].Value;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !new EmailAddressAttribute().IsValid(value) || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value) return false;
        email = value;
        return true;
    }
}
