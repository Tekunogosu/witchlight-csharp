using System;
using Newtonsoft.Json;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// What the world's clock says, in the game's own words.
///
/// Sent rather than filed. A clock is the thing a map has least business writing
/// to a disk: it is stale before the write finishes, and the page asking for it
/// every two seconds is asking a running server, not a file.
/// </summary>
public class LiveWorld
{
    /// <summary>The day and the month, as the game words them — `12. May`.</summary>
    public string Date { get; set; } = "";

    /// <summary>`Year 3`.</summary>
    public string Year { get; set; } = "";

    /// <summary>`14:30`, on the world's own clock.</summary>
    public string Time { get; set; } = "";

    /// <summary>Where spawn is in the year: a season is a fact about a place,
    /// and the hemispheres are in opposite ones.</summary>
    public string Season { get; set; } = "";

}

/// <summary>
/// The world's clock, worded the way the game would word it.
///
/// Its own file rather than a third of the one the markers were in: a date, a
/// season and a time of day are facts about the world, and the only thing they
/// had in common with a waypoint was travelling on the same beat.
/// </summary>
public static class WorldClock
{
    /// <summary>What the world's clock says, as the service wants it.</summary>
    public static string Json(ICoreServerAPI api)
    {
        return JsonConvert.SerializeObject(Now(api));
    }

    /// <summary>
    /// The date, the time and the season, each in the words the game would use.
    ///
    /// Worded here rather than on the page because the game holds the month names
    /// and the operator's language, and a page that spelled them itself would be
    /// spelling them in English on a server that had chosen otherwise.
    /// </summary>
    public static LiveWorld Now(ICoreServerAPI api)
    {
        var calendar = api.World?.Calendar;
        if (calendar is null)
        {
            return new LiveWorld();
        }

        var perMonth = Math.Max(1, calendar.DaysPerMonth);
        var dayOfYear = Math.Max(0, calendar.DayOfYear - 1);
        var month = dayOfYear / perMonth;
        var day = dayOfYear % perMonth + 1;
        var hour = (int)calendar.HourOfDay;
        var minute = (int)((calendar.HourOfDay - hour) * 60);

        return new LiveWorld
        {
            Date = $"{day}. {MonthName(month)}",
            Year = Lang.Get("Year {0}", calendar.Year),
            Time = $"{hour:00}:{minute:00}",
            Season = SeasonAtSpawn(api),
        };
    }

    /// <summary>
    /// The name of a month, counting from zero.
    ///
    /// The game names twelve and a world may be configured with more; one past
    /// the twelve is said by its number rather than left blank or wrapped round
    /// to January, which would be a lie about which month it is.
    /// </summary>
    private static string MonthName(int month)
    {
        string[] names =
        {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December",
        };
        return month >= 0 && month < names.Length
            ? Lang.Get("month-" + names[month])
            : $"{month + 1}";
    }

    private static string SeasonAtSpawn(ICoreServerAPI api)
    {
        try
        {
            var spawn = WorldFacts.Spawn(api);
            if (spawn is not { } at)
            {
                return "";
            }

            return api.World.Calendar?.GetSeason(new BlockPos(at.X, at.Y, at.Z)).ToString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
