using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Witchlight;

/// <summary>
/// Which chunks changed, worded for the map service to pull.
///
/// A list of coordinate pairs and nothing else. What is different about a
/// changed column is answered by pulling it, not by anything this says.
/// </summary>
public static class LiveDirtyFeed
{
    /// <summary>The changed columns, as the service wants them: `[[x, z], ...]`.</summary>
    public static string Json(IEnumerable<(int, int)> columns) =>
        JsonConvert.SerializeObject(columns.Select(c => new[] { c.Item1, c.Item2 }));
}
