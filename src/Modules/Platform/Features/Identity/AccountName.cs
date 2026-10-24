using System.Text;

namespace Wes.Platform.Features.Identity;

/// <summary>Canonical account routing shared by directory and authentication; never applied to passwords.</summary>
public static class AccountName
{
    /// <summary>Trims, applies Unicode compatibility normalization and invariant uppercase; accepts 1..128 scalars without controls.</summary>
    public static string Normalize(string account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var value = account.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var count = value.EnumerateRunes().Count();
        if (count is < 1 or > 128 || value.EnumerateRunes().Any(Rune.IsControl))
            throw new ArgumentException("Account must contain 1..128 non-control Unicode scalars.");
        return value;
    }
}
