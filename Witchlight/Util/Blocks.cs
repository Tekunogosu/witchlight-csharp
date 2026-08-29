using System;

namespace Witchlight;

/// <summary>
/// Which block a position is in.
///
/// Floor rather than a cast, which rounds toward zero and so names the block one
/// to the east and south of the one a negative coordinate is actually in. A
/// Vintage Story world runs from zero, so nothing on a real map reaches that; it
/// is one operation with one right answer, and the players, the markers and
/// anything else that lands a position on the grid ask it here rather than each
/// spelling it out.
/// </summary>
public static class Blocks
{
    public static int At(double position) => (int)Math.Floor(position);
}
