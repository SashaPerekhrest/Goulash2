using System.Globalization;
using System.Text;

namespace Goulash.Application;

/// <summary>
/// Canonical identity keys shared by duplicate lookup and the database advisory lock.
/// </summary>
public static class SupplierIdentity
{
    public static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var decomposed = name.Trim().Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        var previousWasSpace = false;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                continue;

            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace && result.Length > 0)
            {
                result.Append(' ');
                previousWasSpace = true;
            }
        }

        return result.ToString().Trim();
    }

    public static string CreateLockKey(string normalizedName, string? region)
        => $"supplier:{normalizedName.Trim().ToLowerInvariant()}:{(region ?? string.Empty).Trim().ToLowerInvariant()}";

    public static bool IsPotentialNameAndRegionMatch(string firstName, string firstRegion, string secondName, string secondRegion)
        => NormalizeName(firstName) == NormalizeName(secondName)
            && NormalizeName(firstRegion) == NormalizeName(secondRegion);
}
