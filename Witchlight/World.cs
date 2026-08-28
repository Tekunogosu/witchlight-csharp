using System;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>Facts about the world that do not change while it runs.</summary>
public class WorldFacts
{
    /// <summary>
    /// Where the game counts from.
    ///
    /// Vintage Story shows coordinates relative to world spawn, everywhere a
    /// player sees them — the in-game map, the position readout — while the world
    /// itself is a million blocks across with spawn somewhere near the middle. A
    /// map showing absolute positions is not wrong, but it does not agree with
    /// anything the player can compare it to, which amounts to the same thing.
    /// </summary>
    public int SpawnX { get; set; }
    public int SpawnY { get; set; }
    public int SpawnZ { get; set; }

    public string Name { get; set; } = "";

    /// <summary>
    /// Where the world counts from, asked in the one place that owns the answer.
    ///
    /// There are two spawn points and they answer different questions. The world
    /// manager's is the one an admin set explicitly, which on most worlds is
    /// nothing at all — and its getter reads through that nothing, so asking is
    /// not merely empty but throws. The world accessor's is the one the world
    /// actually counts from, near the middle of the map, and it is the one every
    /// coordinate a player reads is relative to.
    ///
    /// Null until the world has finished loading, and null is said rather than
    /// stood in for: a spawn of zero is a perfectly good coordinate, so a guess
    /// cannot be told from an answer.
    /// </summary>
    public static (int X, int Y, int Z)? Spawn(ICoreServerAPI api)
    {
        try
        {
            var at = api.World?.DefaultSpawnPosition?.AsBlockPos;
            return at is null ? null : (at.X, at.Y, at.Z);
        }
        catch (NullReferenceException)
        {
            // Asked before the world has one. Not knowing yet is a state, and
            // every caller here is written to expect it.
            return null;
        }
    }

    /// <summary>
    /// One line for `/witchlight status`.
    ///
    /// Both halves have to be right and neither is visible in game: the world has
    /// to have a spawn point, and it has to have reached the map. A map counting
    /// from absolute zero looks exactly like one counting from spawn until
    /// somebody compares a coordinate against their own screen.
    /// </summary>
    public static string Describe(ICoreServerAPI api, string exports)
    {
        if (Spawn(api) is not { } spawn)
        {
            return "counts from: absolute zero — the world has no readable spawn point";
        }

        return $"counts from: spawn at {spawn.X}, {spawn.Z}"
            + (File.Exists(Path.Combine(exports, "world.json"))
                ? ""
                : "  (world.json not written — the map counts from absolute zero)");
    }

    /// <summary>
    /// Writes them where the map service will find them.
    ///
    /// Called once the world is ready rather than when the mod starts: spawn is
    /// not known that early. Nothing is written when it cannot be read, because a
    /// file saying spawn is the origin reads exactly like a world whose spawn is
    /// the origin — while no file at all is a state the map service names out loud.
    ///
    /// Says whether it managed it, so that a caller asking too early can ask again
    /// rather than leaving the map counting from somewhere the players do not.
    /// </summary>
    public static bool Write(ICoreServerAPI api, string exports)
    {
        try
        {
            var spawn = Spawn(api);
            if (spawn is null)
            {
                return false;
            }

            var facts = new WorldFacts
            {
                SpawnX = spawn.Value.X,
                SpawnY = spawn.Value.Y,
                SpawnZ = spawn.Value.Z,
                Name = api.WorldManager.SaveGame?.WorldName ?? "",
            };

            Disk.Write(
                Path.Combine(exports, "world.json"), JsonConvert.SerializeObject(facts));

            api.Logger.Notification(
                "[witchlight] the map counts from spawn at {0}, {1}", spawn.Value.X, spawn.Value.Z);
            return true;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not write world.json: {0}", error);
            return false;
        }
    }
}
