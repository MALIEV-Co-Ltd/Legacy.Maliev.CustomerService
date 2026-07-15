namespace Legacy.Maliev.CustomerService.Domain;

/// <summary>Legacy customer record.</summary>
public sealed class Customer
{
    /// <summary>The legacy customer identifier referenced by orders and quotations.</summary>
    public int Id { get; set; }
    /// <summary>The customer's given name used in correspondence.</summary>
    public string FirstName { get; set; } = string.Empty;
    /// <summary>The customer's family name used in correspondence.</summary>
    public string LastName { get; set; } = string.Empty;
    /// <summary>The database-computed display name combining given and family names.</summary>
    public string FullName { get; private set; } = string.Empty;
    /// <summary>The customer's landline telephone number, when provided.</summary>
    public string? Telephone { get; set; }
    /// <summary>The customer's mobile telephone number, when provided.</summary>
    public string? Mobile { get; set; }
    /// <summary>The customer's fax number retained for legacy documents.</summary>
    public string? Fax { get; set; }
    /// <summary>The primary email used for account and business communication.</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>The customer's date of birth, when collected.</summary>
    public DateTime? DateOfBirth { get; set; }
    /// <summary>The associated company identifier, when the customer represents a company.</summary>
    public int? CompanyId { get; set; }
    /// <summary>The selected billing address identifier.</summary>
    public int? BillingAddressId { get; set; }
    /// <summary>The selected shipping address identifier.</summary>
    public int? ShippingAddressId { get; set; }
    /// <summary>The UTC timestamp when the customer record was created.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>The UTC timestamp when the customer record was last changed.</summary>
    public DateTime? ModifiedDate { get; set; }
    /// <summary>The selected billing address loaded for customer responses.</summary>
    public Address? BillingAddress { get; set; }
    /// <summary>The associated company loaded for customer responses.</summary>
    public Company? Company { get; set; }
    /// <summary>The selected shipping address loaded for customer responses.</summary>
    public Address? ShippingAddress { get; set; }
}

/// <summary>Legacy company record.</summary>
public sealed class Company
{
    /// <summary>The legacy company identifier.</summary>
    public int Id { get; set; }
    /// <summary>The registered or trading company name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The company's tax registration number, when provided.</summary>
    public string? TaxNumber { get; set; }
    /// <summary>The authority with which the company is registered.</summary>
    public string? Registrar { get; set; }
    /// <summary>The UTC timestamp when the company record was created.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>The UTC timestamp when the company record was last changed.</summary>
    public DateTime? ModifiedDate { get; set; }
    /// <summary>The customers currently associated with this company.</summary>
    public ICollection<Customer> Customers { get; } = [];
}

/// <summary>Legacy customer address record.</summary>
public sealed class Address
{
    /// <summary>The legacy address identifier.</summary>
    public int Id { get; set; }
    /// <summary>The building or premises name, when supplied.</summary>
    public string? Building { get; set; }
    /// <summary>The required primary street-address line.</summary>
    public string AddressLine1 { get; set; } = string.Empty;
    /// <summary>The optional secondary street-address line.</summary>
    public string? AddressLine2 { get; set; }
    /// <summary>The city or locality component.</summary>
    public string? City { get; set; }
    /// <summary>The state, province, or administrative area.</summary>
    public string? State { get; set; }
    /// <summary>The postal or ZIP code.</summary>
    public string? PostalCode { get; set; }
    /// <summary>The legacy country reference identifier.</summary>
    public int CountryId { get; set; }
    /// <summary>The UTC timestamp when the address record was created.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>The UTC timestamp when the address record was last changed.</summary>
    public DateTime? ModifiedDate { get; set; }
    /// <summary>The customers selecting this record as their billing address.</summary>
    public ICollection<Customer> BillingCustomers { get; } = [];
    /// <summary>The customers selecting this record as their shipping address.</summary>
    public ICollection<Customer> ShippingCustomers { get; } = [];
}
