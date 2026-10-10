using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Validates literal company names against the original SQL Server 256 UTF-16-unit input limit.</summary>
public sealed class CompanyNameLengthAttribute : StringLengthAttribute
{
    /// <summary>Retains the declared maximum length while limiting UTF-16 units and validating Unicode scalar wellformedness.</summary>
    public CompanyNameLengthAttribute() : base(256) { }

    /// <inheritdoc />
    public override bool IsValid(object? value) => value is null || value is string name && IsStorable(name);

    /// <summary>Rejects null, malformed UTF-16, NUL and more than 256 UTF-16 units without changing the supplied literal value.</summary>
    /// <param name="value">The literal company name; blank-value admission is enforced by each route.</param>
    /// <returns>Whether the exact value fits the existing company-name column.</returns>
    public static bool IsStorable(string? value)
    {
        if (value is null || value.Length > 256) return false;
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
