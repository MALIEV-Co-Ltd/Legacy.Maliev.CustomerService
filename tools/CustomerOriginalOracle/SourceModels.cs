namespace CustomerOriginalOracle;

#nullable disable
internal sealed class SourceAddress
{
    public SourceAddress()
    {
        CustomerBillingAddress = new HashSet<SourceCustomer>();
        CustomerShippingAddress = new HashSet<SourceCustomer>();
    }

    public int Id { get; set; }
    public string Building { get; set; }
    public string AddressLine1 { get; set; }
    public string AddressLine2 { get; set; }
    public string City { get; set; }
    public string State { get; set; }
    public string PostalCode { get; set; }
    public int CountryId { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<SourceCustomer> CustomerBillingAddress { get; set; }
    public ICollection<SourceCustomer> CustomerShippingAddress { get; set; }
}

internal sealed class SourceCustomer
{
    public int Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string FullName { get; set; }
    public string Telephone { get; set; }
    public string Mobile { get; set; }
    public string Fax { get; set; }
    public string Email { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public int? CompanyId { get; set; }
    public int? BillingAddressId { get; set; }
    public int? ShippingAddressId { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public SourceAddress BillingAddress { get; set; }
    public SourceCompany Company { get; set; }
    public SourceAddress ShippingAddress { get; set; }
}

internal sealed class SourceCompany
{
    public SourceCompany()
    {
        Customer = new HashSet<SourceCustomer>();
    }

    public int Id { get; set; }
    public string Name { get; set; }
    public string TaxNumber { get; set; }
    public string Registrar { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<SourceCustomer> Customer { get; set; }
}
#nullable restore
