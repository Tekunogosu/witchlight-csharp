using System;
using System.Text;

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

    /// <summary>
    /// The pattern a block code is remembered as, unless somebody says otherwise.
    ///
    /// Block codes carry their variant as a number — <c>game:leaves-grown7-oak</c>,
    /// <c>game:water-still-7</c>, <c>game:tallgrass-3</c> — so a preset kept
    /// against one of them answers for exactly one stage of grass out of eight.
    /// Keeping a preset for grass meant keeping it again for every stage of
    /// grass: one preset written down eight times, and eight rows to find when it
    /// changes.
    ///
    /// So the number is where the wildcard goes by default, and
    /// <c>game:leaves-grown*-oak</c> covers the lot. It is only a default: the
    /// star is a character in a text field, so it can be moved, doubled, or taken
    /// out to name one block exactly — <see cref="Fits"/> reads it wherever it
    /// ends up. A code with no number in it is its own pattern, because there is
    /// nothing to widen and widening it further would be guessing.
    ///
    /// The map's own form offers the same thing, so a preset made from a key
    /// press and one made from a right click start out the same — see `widened`
    /// in the viewer's `presets.js`.
    /// </summary>
    public static string Widened(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "";
        }

        var widened = new StringBuilder(code.Length);
        var inNumber = false;
        foreach (var letter in code)
        {
            if (char.IsAsciiDigit(letter))
            {
                if (!inNumber)
                {
                    widened.Append('*');
                    inNumber = true;
                }
                continue;
            }

            inNumber = false;
            widened.Append(letter);
        }

        return widened.ToString();
    }
}
