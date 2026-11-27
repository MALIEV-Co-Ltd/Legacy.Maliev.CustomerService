using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Validates optional Company fields without mutating their literal values.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class CompanyFieldCapacityAttribute : ValidationAttribute
{
    /// <summary>Reports an invalid field without echoing its supplied value.</summary>
    public CompanyFieldCapacityAttribute() : base("The company field exceeds the supported storage capacity.") { }

    /// <inheritdoc />
    public override bool IsValid(object? value) => value is null || value is string text && IsWithinSourceCapacity(text);

    /// <summary>Permits only U+0020 beyond the first 256 UTF-16 units, retaining all literal input.</summary>
    /// <param name="value">The nullable input text.</param>
    /// <returns>Whether the value satisfies the qualified capacity rule.</returns>
    public static bool IsWithinSourceCapacity(string? value)
    {
        if (value is null) return true;
        for (var index = 256; index < value.Length; index++)
        {
            if (value[index] != ' ') return false;
        }
        return true;
    }
}
