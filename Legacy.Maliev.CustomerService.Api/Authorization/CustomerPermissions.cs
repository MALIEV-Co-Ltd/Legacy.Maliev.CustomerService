namespace Legacy.Maliev.CustomerService.Api.Authorization;

/// <summary>Granular legacy customer permissions.</summary>
public static class CustomerPermissions
{
    /// <summary>Allows reading a specific legacy customer profile.</summary>
    public const string CustomersRead = "legacy-customer.customers.read";

    /// <summary>Allows listing and searching legacy customer profiles.</summary>
    public const string CustomersList = "legacy-customer.customers.list";

    /// <summary>Allows creating a legacy customer profile.</summary>
    public const string CustomersCreate = "legacy-customer.customers.create";

    /// <summary>Allows changing an existing legacy customer profile.</summary>
    public const string CustomersUpdate = "legacy-customer.customers.update";

    /// <summary>Allows deleting a legacy customer profile.</summary>
    public const string CustomersDelete = "legacy-customer.customers.delete";

    /// <summary>Allows reading a specific customer address.</summary>
    public const string AddressesRead = "legacy-customer.addresses.read";

    /// <summary>Allows listing addresses across legacy customer records.</summary>
    public const string AddressesList = "legacy-customer.addresses.list";

    /// <summary>Allows adding an address to a customer profile.</summary>
    public const string AddressesCreate = "legacy-customer.addresses.create";

    /// <summary>Allows changing an existing customer address.</summary>
    public const string AddressesUpdate = "legacy-customer.addresses.update";

    /// <summary>Allows deleting an address from a customer profile.</summary>
    public const string AddressesDelete = "legacy-customer.addresses.delete";

    /// <summary>Allows reading a specific company associated with legacy customers.</summary>
    public const string CompaniesRead = "legacy-customer.companies.read";

    /// <summary>Allows creating a company record used by legacy customer profiles.</summary>
    public const string CompaniesCreate = "legacy-customer.companies.create";

    /// <summary>Allows changing an existing company record.</summary>
    public const string CompaniesUpdate = "legacy-customer.companies.update";

    /// <summary>Allows deleting a company record from the legacy customer domain.</summary>
    public const string CompaniesDelete = "legacy-customer.companies.delete";

}
