namespace Legacy.Maliev.CustomerService.Data;

/// <summary>A committed customer-create response bound to one request key and actor.</summary>
public sealed class CustomerCreateOperation
{
    /// <summary>The caller-supplied operation key.</summary>
    public Guid Key { get; set; }
    /// <summary>SHA-256 of the authenticated actor identifier.</summary>
    public string ActorHash { get; set; } = string.Empty;
    /// <summary>SHA-256 of the exact customer-create request value.</summary>
    public string RequestHash { get; set; } = string.Empty;
    /// <summary>The original legacy customer identifier.</summary>
    public int CustomerId { get; set; }
    /// <summary>The original customer response, retained even if the profile later changes.</summary>
    public string ResponseJson { get; set; } = string.Empty;
    /// <summary>When the operation committed.</summary>
    public DateTime CreatedAt { get; set; }
}
