using System;

namespace Witchlight;

/// <summary>
/// Whether a preset's pattern names a block.
///
/// <c>*</c> stands for any run of characters and everything else is itself,
/// which is the whole grammar. A pattern is read by whoever typed it, and one
/// that needed escaping rules would be a pattern nobody could check by eye — so
/// <c>game:ore-*-nativecopper-*</c> reaches copper in every rock, and
/// <c>game:rock</c> reaches one block rather than every rock there is.
///
/// The map's own page holds the other copy of this, in <c>presets.js</c>. Two
/// copies of one rule is ordinarily a defect, and here it is the price of the
/// two sides being different languages: the page matches against the block under
/// a pointer and this matches against the block under a player, and neither can
/// wait on the other to answer a keypress. Change one and change the other; the
/// tests on each side are written from the same table of cases.
/// </summary>
public static class BlockPattern
{
    /// <summary>Whether this pattern names this block code.</summary>
    public static bool Fits(string? pattern, string? code)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(code))
        {
            return false;
        }

        var parts = pattern.ToLowerInvariant().Split('*');
        var named = code.ToLowerInvariant();
        var reached = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            var found = i == 0
                ? (named.StartsWith(part, StringComparison.Ordinal) ? 0 : -1)
                : named.IndexOf(part, reached, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }
            reached = found + part.Length;
        }

        // A pattern not ending in `*` has to reach the end, or `rock-*` and
        // `rock` would both answer for every rock there is.
        var last = parts[^1];
        return last.Length == 0 || named.EndsWith(last, StringComparison.Ordinal);
    }
}
