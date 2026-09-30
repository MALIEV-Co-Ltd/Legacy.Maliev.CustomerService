namespace Legacy.Maliev.CustomerService.Domain;

/// <summary>Durable replay boundary for one owner-bound profile completion.</summary>
public sealed class QuotationProfileCompletionOperation
{
    /// <summary>The target customer, scoped independently of email.</summary>
    public int CustomerId { get; set; }
    /// <summary>The stable submission-derived operation key.</summary>
    public Guid Key { get; set; }
    /// <summary>The hash of the authenticated subject.</summary>
    public string ActorHash { get; set; } = string.Empty;
    /// <summary>The normalized request digest.</summary>
    public string RequestHash { get; set; } = string.Empty;
    /// <summary>The immutable receipt identifier.</summary>
    public Guid CompletionId { get; set; }
    /// <summary>Whether the committed graph changed.</summary>
    public bool Changed { get; set; }
    /// <summary>The UTC creation time.</summary>
    public DateTime CreatedAt { get; set; }
}
