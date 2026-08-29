using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Running a piece of work, and never letting it out.
///
/// A game tick listener that throws does not merely fail that tick. The server
/// records a listener as having run only after its handler returns — the store
/// comes after the call, with nothing to catch in between — so a handler that
/// throws leaves the listener permanently due and it fires again on the very next
/// pass of the server loop. That turned one unready entity into a hundred
/// thousand identical errors in four seconds, which is the server's own error
/// threshold, and it shut itself down.
///
/// Nothing this mod does is worth a server. Each kind of failure is reported once
/// and then held quiet: the hundredth copy of a stack trace says nothing the
/// first did not, and burying the log is its own kind of outage. A different
/// exception from the same work is a different failure and is said.
/// </summary>
public sealed class Faults
{
    private readonly ILogger _log;

    /// <summary>Which failures have already been said, so none is said twice.</summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public Faults(ILogger log)
    {
        _log = log;
    }

    /// <summary>Runs the work, and swallows whatever comes out of it.</summary>
    public void Doing(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception error)
        {
            if (_reported.Add($"{what}/{error.GetType().Name}"))
            {
                _log.Error(
                    "[witchlight] {0} failed, and this will not be reported again: {1}", what, error);
            }
        }
    }
}
