using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Validates literal company names against the qualified SQL Server 256 UTF-16-unit storage boundary.</summary>
public sealed class CompanyNameLengthAttribute : ValidationAttribute
{
    /// <summary>Reports an invalid name without echoing the supplied value.</summary>
    public CompanyNameLengthAttribute() : base("The company name exceeds the supported storage capacity or contains invalid Unicode.") { }

    /// <inheritdoc />
    public override bool IsValid(object? value) => value is null || value is string name && IsStorable(name);

    /// <summary>Rejects null, malformed UTF-16, NUL and nonspace overflow without changing the supplied literal value.</summary>
    /// <param name="value">The literal company name; blank-value admission is enforced by each route.</param>
    /// <returns>Whether the literal is accepted by the qualified company-name storage boundary.</returns>
    public static bool IsStorable(string? value)
    {
        if (value is null || !CompanyFieldCapacityAttribute.IsWithinSourceCapacity(value)) return false;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                rune.Value == 0) return false;
            remaining = remaining[consumed..];
        }

        return true;
    }
}
