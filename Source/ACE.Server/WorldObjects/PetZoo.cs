using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Animation;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Pet Zoo. Players drag a pet egg (hatched or not) onto Jonathan; he keeps it forever and
    /// the creature joins the zoo -- one of each exact creature (WCID). Each animal lives in ONE
    /// room (admin-placed center point(s) + wander radius) and strolls around inside it. Oversized
    /// creatures only go to rooms whose name starts with "roof". Can't be attacked, shows
    /// "Donated to the zoo by X", twitches when double-clicked.
    ///
    /// Hooks: Player.GiveObjectToNPC, Player.TryUseItem, Landblock.CreateWorldObjects, Pet.SlowTick.
    /// State saved to zoo.json next to ace.server.exe (no DB changes; each server keeps its own).
    /// </summary>
    public static class PetZoo
    {
        // ---- WCIDs ----
        public const uint JonathanWcid = 9871198;   // Jonathan the zookeeper (clone of chip vendor 9871099)
        public const uint EggWcid = 9870772;        // the pet egg (same one creatures drop)

        // ---- Tuning ----
        private const float SpeechRange = 20f;
        public const float DefaultBigRadius = 2.0f;    // creature physics radius (m) above this = roof only
        public const float DefaultRoomRadius = 3.0f;   // how far from a room's center point animals stroll

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public class Point
        {
            public uint Cell { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float Qz { get; set; }
            public float Qw { get; set; }

            public Position ToPosition() => new Position(Cell, X, Y, Z, 0f, 0f, Qz, Qw);
            public ushort Landblock => (ushort)(Cell >> 16);
        }

        public class Room
        {
            public List<Point> Points { get; set; } = new List<Point>();
            public float Radius { get; set; } = DefaultRoomRadius;
        }

        public class Animal
        {
            public uint Wcid { get; set; }          // exact creature -- the uniqueness key
            public uint ShellWcid { get; set; }     // the egg's pet shell (harmless base object)
            public string Donor { get; set; }
            public double DonatedAt { get; set; }
            public string ObjDesc { get; set; }     // captured look (clothes/palette), may be null
            public string Room { get; set; }
            public bool WakeOnApproach { get; set; }   // stays dormant (peace stance) until a player comes near -- slithis/tendrils

            [System.Text.Json.Serialization.JsonIgnore]
            public ZooAnimal Live;
        }

        /// <summary>One reward egg handed out by Jonathan.</summary>
        public class Reward
        {
            public string Player { get; set; }      // who received it
            public uint Wcid { get; set; }          // the random monster on the egg
            public string Monster { get; set; }
            public string ForDonation { get; set; } // the creature they donated to earn it
            public double GivenAt { get; set; }     // unix seconds
            public uint EggGuid { get; set; }       // the egg item's id
        }

        public class Data
        {
            public Dictionary<string, Room> Rooms { get; set; } = new Dictionary<string, Room>();
            public float BigRadius { get; set; } = DefaultBigRadius;
            public List<uint> Blocked { get; set; } = new List<uint>();   // monsters never given as reward eggs
            public List<Animal> Animals { get; set; } = new List<Animal>();
            public List<Reward> Rewards { get; set; } = new List<Reward>();
        }

        private static Data data = new Data();
        private static readonly object dataLock = new object();
        private static bool loaded;
        // Saved OUTSIDE the build folder so a rebuild/redeploy can't wipe it:
        //   Windows: C:\ProgramData\ACE\zoo.json
        // (The old location, next to ace.server.exe, is copied over once if it's still there.)
        private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ACE");
        private static string FilePath => Path.Combine(DataFolder, "zoo.json");
        private static string OldFilePath => Path.Combine(AppContext.BaseDirectory, "zoo.json");

        public static bool IsRoofRoom(string name) => name.StartsWith("roof", StringComparison.OrdinalIgnoreCase);

        // =====================================================================
        // Jonathan: donations
        // =====================================================================

        /// <summary>Called from Player.GiveObjectToNPC. Returns true if fully handled here.</summary>
        public static bool TryHandleGive(Player player, WorldObject npc, WorldObject item)
        {
            if (npc.WeenieClassId != JonathanWcid || item is not PetDevice egg)
                return false;

            EnsureLoaded();

            // eggs the zoo gave out as rewards can never come back as donations
            if (egg.GetProperty(PropertyBool.ZooGiftPet) == true)
            {
                Refuse(player, npc, item, "This one came from our zoo, I'm afraid I can't take it back!");
                return true;
            }

            var wcid = egg.GetProperty(Pet.SourceWcidProp) ?? 0;
            if (wcid == 0 && egg.PetClass != null)
                wcid = (uint)egg.PetClass.Value;
            var shell = (uint)(egg.PetClass ?? 0);

            var weenie = wcid != 0 ? DatabaseManager.World.GetCachedWeenie(wcid) : null;
            var shellWeenie = shell != 0 ? DatabaseManager.World.GetCachedWeenie(shell) : null;

            if (weenie == null || shellWeenie == null)
            {
                Refuse(player, npc, item, "Hmm, I can't tell what kind of creature this is. I'm sorry, I can't take it.");
                return true;
            }

            var creatureName = weenie.GetProperty(PropertyString.Name) ?? "creature";

            lock (dataLock)
            {
                if (data.Animals.Any(a => a.Wcid == wcid))
                {
                    Refuse(player, npc, item, $"We already have a {creatureName} in the zoo. Thank you though!");
                    return true;
                }
            }

            var room = PickRoom(weenie);
            if (room == null)
            {
                Refuse(player, npc, item, "The zoo isn't ready for new animals yet. Please come back later!");
                return true;
            }

            // the reward egg needs somewhere to go
            if (player.GetFreeInventorySlots() < 1)
            {
                Refuse(player, npc, item, "Make a little room in your pack first, I have a gift for you!");
                return true;
            }

            if (!player.TryConsumeFromInventoryWithNetworking(item, 1))
            {
                player.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(player.Session, item.Guid.Full));
                return true;
            }

            var animal = new Animal
            {
                Wcid = wcid,
                ShellWcid = shell,
                Donor = player.Name,
                DonatedAt = Time.GetUnixTime(),
                ObjDesc = egg.GetProperty(Pet.PetObjDescProp),
                Room = room,
            };

            lock (dataLock) data.Animals.Add(animal);
            Save();

            player.SendMessage($"You give your {item.Name} to {npc.Name}.");
            Say(npc, $"Thank you, {player.Name}! The {creatureName} will have a wonderful home here.");

            Spawn(animal);
            GiveRandomEgg(player, npc, creatureName);
            return true;
        }

        // ---- reward: one egg of a completely random monster ----

        private static List<uint> monsterWcids;
        private static readonly object monsterLock = new object();

        /// <summary>Every attackable creature weenie in the world DB (built once, on first use).</summary>
        private static List<uint> GetMonsterWcids()
        {
            if (monsterWcids != null)
                return monsterWcids;

            lock (monsterLock)
            {
                if (monsterWcids != null)
                    return monsterWcids;

                var list = new List<uint>();
                try
                {
                    List<uint> ids;
                    using (var ctx = new ACE.Database.Models.World.WorldDbContext())
                        ids = ctx.Weenie.Where(w => w.Type == (int)WeenieType.Creature).Select(w => w.ClassId).ToList();

                    foreach (var id in ids)
                    {
                        var w = DatabaseManager.World.GetCachedWeenie(id);
                        if (w == null) continue;
                        if (string.IsNullOrWhiteSpace(w.GetProperty(PropertyString.Name))) continue;
                        if ((w.GetProperty(PropertyDataId.Setup) ?? 0) == 0) continue;
                        if (w.GetProperty(PropertyBool.Attackable) == false) continue;          // NPCs
                        if (w.GetProperty(PropertyBool.NpcLooksLikeObject) == true) continue;
                        list.Add(id);
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"PetZoo - failed to build monster list: {ex.Message}");
                }

                log.Info($"PetZoo - {list.Count} monsters available for reward eggs");
                monsterWcids = list;
                return monsterWcids;
            }
        }

        /// <summary>Starts building the monster list on a background thread (it reads thousands of weenies).</summary>
        public static void PrebuildMonsterList()
        {
            if (monsterWcids == null)
                System.Threading.Tasks.Task.Run(() => GetMonsterWcids());
        }

        private static void GiveRandomEgg(Player player, WorldObject npc, string donatedName)
        {
            // The first build reads every creature weenie from the DB. Doing that on the game thread
            // froze the server long enough to drop the player's connection, so build it off-thread
            // and hand the egg over once it's ready.
            if (monsterWcids == null)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    GetMonsterWcids();
                    new ActionChain(player, () => GiveRandomEggNow(player, npc, donatedName)).EnqueueChain();
                });
                return;
            }

            GiveRandomEggNow(player, npc, donatedName);
        }

        private static void GiveRandomEggNow(Player player, WorldObject npc, string donatedName)
        {
            if (player.Session == null)
                return;   // logged off while the list was building

            var pool = GetMonsterWcids();   // outside dataLock: may build the list on first use
            List<uint> list;
            lock (dataLock)
                list = pool.Where(w => !data.Blocked.Contains(w)).ToList();
            if (list.Count == 0)
                return;

            for (var tries = 0; tries < 10; tries++)
            {
                var wcid = list[ThreadSafeRandom.Next(0, list.Count - 1)];

                Creature source = null;
                try
                {
                    // built off-world only so the egg can snapshot its real look (clothes/palette); never enters the world
                    source = WorldObjectFactory.CreateNewWorldObject(wcid) as Creature;
                }
                catch (Exception ex)
                {
                    log.Warn($"PetZoo - couldn't build creature {wcid} for a reward egg: {ex.Message}");
                }
                if (source == null)
                    continue;

                var egg = WorldObjectFactory.CreateNewWorldObject(EggWcid);
                if (egg == null)
                {
                    source.Destroy();
                    return;
                }

                Pet.ImprintCreature(egg, source);
                Pet.EnsurePetXpDisplay(egg);
                egg.SetProperty(PropertyBool.ZooGiftPet, true);
                if (egg is PetDevice giftEgg)
                    Pet.UpdatePetDeviceDescription(giftEgg);   // ID panel shows it came from the zoo
                source.Destroy();

                if (player.TryCreateInInventoryWithNetworking(egg, out _, true))
                {
                    Say(npc, $"Here's a little something to raise yourself. Take good care of it!");
                    player.SendMessage($"{npc.Name} gives you {egg.Name}.");

                    lock (dataLock)
                        data.Rewards.Add(new Reward
                        {
                            Player = player.Name,
                            Wcid = wcid,
                            Monster = DatabaseManager.World.GetCachedWeenie(wcid)?.GetProperty(PropertyString.Name) ?? "?",
                            ForDonation = donatedName,
                            GivenAt = Time.GetUnixTime(),
                            EggGuid = egg.Guid.Full,
                        });
                    Save();
                }
                else
                    egg.Destroy();
                return;
            }
        }

        private static void Refuse(Player player, WorldObject npc, WorldObject item, string text)
        {
            player.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(player.Session, item.Guid.Full));
            Say(npc, text);
        }

        /// <summary>Oversized -> a random roof room. Others -> a random non-roof room (any room if there are none).</summary>
        private static string PickRoom(Weenie creature)
        {
            var big = GetRadius(creature) > data.BigRadius;

            lock (dataLock)
            {
                var usable = data.Rooms.Where(r => r.Value.Points.Count > 0).Select(r => r.Key).ToList();
                var pool = big ? usable.Where(IsRoofRoom).ToList() : usable.Where(r => !IsRoofRoom(r)).ToList();
                if (!big && pool.Count == 0)
                    pool = usable;

                return pool.Count > 0 ? pool[ThreadSafeRandom.Next(0, pool.Count - 1)] : null;
            }
        }

        /// <summary>Physics radius of a creature at its full size (setup sphere x DefaultScale).</summary>
        public static float GetRadius(Weenie creature)
        {
            var setupId = creature.GetProperty(PropertyDataId.Setup) ?? 0;
            if (setupId == 0)
                return 0f;

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupId);
            if (setup == null)
                return 0f;

            var r = setup.Spheres.Count > 0 ? setup.Spheres[0].Radius
                  : setup.CylSpheres.Count > 0 ? setup.CylSpheres[0].Radius
                  : setup.Radius;
            var scale = (float)(creature.GetProperty(PropertyFloat.DefaultScale) ?? 1.0);
            return r * scale;
        }

        // =====================================================================
        // Spawning
        // =====================================================================

        /// <summary>Called from Landblock.CreateWorldObjects once the landblock's objects are in.</summary>
        public static void OnLandblockLoaded(Landblock landblock)
        {
            EnsureLoaded();
            PrebuildMonsterList();   // first landblock load at startup -> list is ready long before anyone donates

            List<Animal> toSpawn;
            lock (dataLock)
            {
                toSpawn = data.Animals.Where(a => (a.Live == null || a.Live.IsDestroyed)
                    && GetRoom(a.Room) is Room r && r.Points.Any(p => p.Landblock == landblock.Id.Landblock)).ToList();
            }

            foreach (var a in toSpawn)
                Spawn(a);
        }

        private static void Spawn(Animal a)
        {
            if (a.Live != null && !a.Live.IsDestroyed)
                return;

            var room = GetRoom(a.Room);
            if (room == null || room.Points.Count == 0)
                return;

            var shellWeenie = DatabaseManager.World.GetCachedWeenie(a.ShellWcid);
            var creature = DatabaseManager.World.GetCachedWeenie(a.Wcid);
            if (shellWeenie == null || creature == null)
            {
                log.Error($"PetZoo.Spawn - missing weenie (creature {a.Wcid}, shell {a.ShellWcid})");
                return;
            }

            var z = new ZooAnimal(shellWeenie, GuidManager.NewDynamicGuid());
            if (z.Init(a, creature, room.Points[ThreadSafeRandom.Next(0, room.Points.Count - 1)]))
                a.Live = z;
            else
                z.Destroy();
        }

        /// <summary>A copy of the named room (null if it doesn't exist).</summary>
        public static Room GetRoom(string name)
        {
            lock (dataLock)
            {
                if (name == null || !data.Rooms.TryGetValue(name, out var r))
                    return null;
                return new Room { Points = r.Points.ToList(), Radius = r.Radius };
            }
        }

        // =====================================================================
        // Double-click
        // =====================================================================

        /// <summary>Called from Player.TryUseItem. Returns true if the item was a zoo animal.</summary>
        public static bool TryHandleUse(Player player, WorldObject item)
        {
            if (item is not ZooAnimal z)
                return false;

            z.DoTwitch(player);
            return true;
        }

        // =====================================================================
        // Admin (ZooCommands)
        // =====================================================================

        /// <summary>Adds a center point (where the admin stands) to a room, creating the room if needed.</summary>
        public static string AddRoomPoint(Player admin, string room, float? radius)
        {
            EnsureLoaded();
            var loc = admin.Location;
            var p = new Point { Cell = loc.Cell, X = loc.PositionX, Y = loc.PositionY, Z = loc.PositionZ, Qz = loc.RotationZ, Qw = loc.RotationW };

            int count; float r;
            lock (dataLock)
            {
                if (!data.Rooms.TryGetValue(room, out var rm))
                    data.Rooms[room] = rm = new Room();
                rm.Points.Add(p);
                if (radius != null) rm.Radius = radius.Value;
                count = rm.Points.Count; r = rm.Radius;
            }
            Save();
            return $"Room '{room}': point #{count} added, wander radius {r:0.##}m{(IsRoofRoom(room) ? " (roof room: big creatures go here)" : "")}.";
        }

        public static string SetRoomRadius(string room, float radius)
        {
            EnsureLoaded();
            lock (dataLock)
            {
                if (!data.Rooms.TryGetValue(room, out var rm))
                    return $"No room named '{room}'.";
                rm.Radius = radius;
            }
            Save();
            return $"Room '{room}' wander radius is now {radius:0.##}m.";
        }

        public static string ClearRoom(string room)
        {
            EnsureLoaded();
            bool removed;
            lock (dataLock) removed = data.Rooms.Remove(room);
            Save();
            return removed ? $"Room '{room}' deleted. Animals assigned to it stay hidden until you @zoo move them." : $"No room named '{room}'.";
        }

        public static string SetBigRadius(float r)
        {
            EnsureLoaded();
            lock (dataLock) data.BigRadius = r;
            Save();
            return $"Creatures with a radius above {r:0.##}m now go to roof rooms (applies to new donations).";
        }

        public static string Info()
        {
            EnsureLoaded();
            var sb = new System.Text.StringBuilder();
            lock (dataLock)
            {
                sb.AppendLine($"Jonathan wcid: {JonathanWcid}   big radius: {data.BigRadius:0.##}m");
                foreach (var kv in data.Rooms)
                {
                    var n = data.Animals.Count(a => a.Room == kv.Key);
                    sb.AppendLine($"Room '{kv.Key}': {kv.Value.Points.Count} point(s), radius {kv.Value.Radius:0.##}m, {n} animal(s){(IsRoofRoom(kv.Key) ? " [roof]" : "")}");
                }
                sb.AppendLine($"{data.Animals.Count} animals:");
                foreach (var a in data.Animals)
                {
                    var name = DatabaseManager.World.GetCachedWeenie(a.Wcid)?.GetProperty(PropertyString.Name) ?? "?";
                    var live = a.Live != null && !a.Live.IsDestroyed ? "live" : "not spawned";
                    sb.AppendLine($"  {name} ({a.Wcid}) - room '{a.Room}' - by {a.Donor} - {live}");
                }
                sb.AppendLine($"{data.Rewards.Count} reward eggs given out (@zoo rewards to list them).");
            }
            return sb.ToString();
        }

        /// <summary>Lists reward eggs, newest first. Optional player name filter.</summary>
        // ---- reward pool blocklist ----

        public static string Block(uint wcid, bool block)
        {
            EnsureLoaded();
            var name = DatabaseManager.World.GetCachedWeenie(wcid)?.GetProperty(PropertyString.Name);
            if (name == null)
                return $"No weenie {wcid} found.";

            lock (dataLock)
            {
                if (block && !data.Blocked.Contains(wcid)) data.Blocked.Add(wcid);
                if (!block) data.Blocked.Remove(wcid);
            }
            Save();
            return block ? $"{name} ({wcid}) can no longer come from zoo reward eggs." : $"{name} ({wcid}) is back in the reward pool.";
        }

        public static string ListBlocked()
        {
            EnsureLoaded();
            var sb = new System.Text.StringBuilder();
            lock (dataLock)
            {
                sb.AppendLine($"{data.Blocked.Count} monster(s) blocked from reward eggs:");
                foreach (var w in data.Blocked)
                    sb.AppendLine($"  {DatabaseManager.World.GetCachedWeenie(w)?.GetProperty(PropertyString.Name) ?? "?"} ({w})");
            }
            return sb.ToString();
        }

        /// <summary>Rebuilds the reward pool from the database (picks up creatures added since startup).</summary>
        public static string RebuildPool()
        {
            lock (monsterLock)
                monsterWcids = null;
            System.Threading.Tasks.Task.Run(() => GetMonsterWcids());
            return "Rebuilding the reward pool in the background; the server log shows the new count when done.";
        }

        public static string ListRewards(string playerName)
        {
            EnsureLoaded();
            var sb = new System.Text.StringBuilder();
            lock (dataLock)
            {
                var list = data.Rewards.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(playerName))
                    list = list.Where(r => string.Equals(r.Player, playerName, StringComparison.OrdinalIgnoreCase));
                var all = list.Reverse().ToList();

                sb.AppendLine($"{all.Count} reward egg(s){(string.IsNullOrWhiteSpace(playerName) ? "" : $" for {playerName}")}:");
                foreach (var r in all.Take(25))
                {
                    var when = DateTimeOffset.FromUnixTimeSeconds((long)r.GivenAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                    sb.AppendLine($"  {when} - {r.Player} got {r.Monster} ({r.Wcid}) for donating {r.ForDonation}");
                }
                if (all.Count > 25)
                    sb.AppendLine($"  ...and {all.Count - 25} older. Add a player name to narrow it down.");
            }
            return sb.ToString();
        }

        private static Animal FindAnimal(string name)
        {
            lock (dataLock)
                return data.Animals.FirstOrDefault(x => string.Equals(DatabaseManager.World.GetCachedWeenie(x.Wcid)?.GetProperty(PropertyString.Name), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Moves one animal (by name) to another room; respawns it there.</summary>
        public static string MoveAnimal(string name, string room)
        {
            EnsureLoaded();
            var a = FindAnimal(name);
            if (a == null)
                return $"No zoo animal named {name}.";
            if (GetRoom(room) is not Room r || r.Points.Count == 0)
                return $"No room named '{room}'.";

            a.Room = room;
            Save();
            a.Live?.Destroy();
            a.Live = null;
            Spawn(a);
            return $"{name} moved to room '{room}'.";
        }

        /// <summary>Turns "rise up when a player comes near" on/off for one animal.</summary>
        public static string SetWakeOnApproach(string name, bool on)
        {
            EnsureLoaded();
            var a = FindAnimal(name);
            if (a == null)
                return $"No zoo animal named {name}.";

            a.WakeOnApproach = on;
            Save();
            a.Live?.Destroy();
            a.Live = null;
            Spawn(a);
            return on ? $"{name} now stays dormant until a player comes within {ZooAnimal.WakeRange:0}m." : $"{name} no longer waits dormant.";
        }

        /// <summary>Removes one animal (by name) from the zoo for good.</summary>
        public static string RemoveAnimal(string name)
        {
            EnsureLoaded();
            var a = FindAnimal(name);
            if (a == null)
                return $"No zoo animal named {name}.";

            lock (dataLock) data.Animals.Remove(a);
            Save();
            a.Live?.Destroy();
            return $"{name} removed from the zoo.";
        }

        /// <summary>Despawns and respawns every animal (after changing rooms).</summary>
        public static string RespawnAll()
        {
            EnsureLoaded();
            List<Animal> all;
            lock (dataLock) all = data.Animals.ToList();
            foreach (var a in all)
            {
                a.Live?.Destroy();
                a.Live = null;
                Spawn(a);
            }
            return $"Respawned {all.Count} zoo animals.";
        }

        // =====================================================================
        // Helpers / persistence
        // =====================================================================

        private static void Say(WorldObject npc, string text)
        {
            npc.EnqueueBroadcast(new GameMessageHearSpeech(text, npc.Name, npc.Guid.Full, ChatMessageType.Speech), SpeechRange);
        }

        // =====================================================================
        // Persistence: shard database tables zoo_* (see PET_ZOO_GUIDE.md for the CREATE TABLE SQL).
        // Whole state is small, so each save rewrites all zoo rows in one transaction, on a
        // background thread (never blocks the game). A zoo.json from the old file-based version is
        // imported once if the tables are empty.
        // =====================================================================

        private static bool loadFailed;          // tables missing/unreachable -> never save (would wipe)
        private static readonly object saveLock = new object();
        private static long saveVersion, writtenVersion;

        private static MySqlConnector.MySqlConnection OpenShard()
        {
            var c = ConfigManager.Config.MySql.Shard;
            var conn = new MySqlConnector.MySqlConnection($"server={c.Host};port={c.Port};user={c.Username};password={c.Password};database={c.Database};{c.ConnectionOptions}");
            conn.Open();
            return conn;
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (dataLock)
            {
                if (loaded) return;
                try
                {
                    data = LoadFromDb();
                    log.Info($"PetZoo - loaded from shard DB: {data.Rooms.Count} rooms, {data.Animals.Count} animals, {data.Rewards.Count} rewards, {data.Blocked.Count} blocked");

                    // one-time import from the old zoo.json
                    if (data.Rooms.Count == 0 && data.Animals.Count == 0 && data.Rewards.Count == 0)
                    {
                        var json = File.Exists(FilePath) ? FilePath : File.Exists(OldFilePath) ? OldFilePath : null;
                        if (json != null)
                        {
                            data = JsonSerializer.Deserialize<Data>(File.ReadAllText(json)) ?? new Data();
                            WriteToDb(data);
                            File.Move(json, json + ".imported", true);
                            log.Info($"PetZoo - imported {json} into the shard DB (renamed to zoo.json.imported)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    loadFailed = true;
                    log.Error($"PetZoo - FAILED to load from the shard DB ({ex.Message}). Zoo changes will NOT be saved until this is fixed. Did the zoo_* tables get created?");
                }
                loaded = true;
            }
        }

        private static Data LoadFromDb()
        {
            var d = new Data();
            using var conn = OpenShard();

            using (var cmd = new MySqlConnector.MySqlCommand("SELECT `key`, `value` FROM zoo_setting", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    if (r.GetString(0) == "BigRadius" && float.TryParse(r.GetString(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var br))
                        d.BigRadius = br;

            using (var cmd = new MySqlConnector.MySqlCommand("SELECT name, radius FROM zoo_room", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    d.Rooms[r.GetString(0)] = new Room { Radius = r.GetFloat(1) };

            using (var cmd = new MySqlConnector.MySqlCommand("SELECT room, cell, x, y, z, qz, qw FROM zoo_room_point ORDER BY id", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (!d.Rooms.TryGetValue(r.GetString(0), out var room))
                        d.Rooms[r.GetString(0)] = room = new Room();
                    room.Points.Add(new Point { Cell = r.GetUInt32(1), X = r.GetFloat(2), Y = r.GetFloat(3), Z = r.GetFloat(4), Qz = r.GetFloat(5), Qw = r.GetFloat(6) });
                }

            using (var cmd = new MySqlConnector.MySqlCommand("SELECT wcid, shell_wcid, donor, donated_at, obj_desc, room, wake_on_approach FROM zoo_animal ORDER BY donated_at", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    d.Animals.Add(new Animal
                    {
                        Wcid = r.GetUInt32(0),
                        ShellWcid = r.GetUInt32(1),
                        Donor = r.GetString(2),
                        DonatedAt = r.GetDouble(3),
                        ObjDesc = r.IsDBNull(4) ? null : r.GetString(4),
                        Room = r.GetString(5),
                        WakeOnApproach = r.GetBoolean(6),
                    });

            using (var cmd = new MySqlConnector.MySqlCommand("SELECT player, wcid, monster, for_donation, given_at, egg_guid FROM zoo_reward ORDER BY id", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    d.Rewards.Add(new Reward
                    {
                        Player = r.GetString(0),
                        Wcid = r.GetUInt32(1),
                        Monster = r.GetString(2),
                        ForDonation = r.IsDBNull(3) ? null : r.GetString(3),
                        GivenAt = r.GetDouble(4),
                        EggGuid = r.GetUInt32(5),
                    });

            using (var cmd = new MySqlConnector.MySqlCommand("SELECT wcid FROM zoo_blocked", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    d.Blocked.Add(r.GetUInt32(0));

            return d;
        }

        private static void WriteToDb(Data d)
        {
            using var conn = OpenShard();
            using var tx = conn.BeginTransaction();

            void Exec(string sql, params (string, object)[] args)
            {
                using var cmd = new MySqlConnector.MySqlCommand(sql, conn, tx);
                foreach (var (n, v) in args)
                    cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            foreach (var t in new[] { "zoo_setting", "zoo_room_point", "zoo_room", "zoo_animal", "zoo_reward", "zoo_blocked" })
                Exec($"DELETE FROM {t}");

            Exec("INSERT INTO zoo_setting (`key`, `value`) VALUES ('BigRadius', @v)",
                ("@v", d.BigRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            foreach (var kv in d.Rooms)
            {
                Exec("INSERT INTO zoo_room (name, radius) VALUES (@n, @r)", ("@n", kv.Key), ("@r", kv.Value.Radius));
                foreach (var p in kv.Value.Points)
                    Exec("INSERT INTO zoo_room_point (room, cell, x, y, z, qz, qw) VALUES (@n, @c, @x, @y, @z, @qz, @qw)",
                        ("@n", kv.Key), ("@c", p.Cell), ("@x", p.X), ("@y", p.Y), ("@z", p.Z), ("@qz", p.Qz), ("@qw", p.Qw));
            }

            foreach (var a in d.Animals)
                Exec("INSERT INTO zoo_animal (wcid, shell_wcid, donor, donated_at, obj_desc, room, wake_on_approach) VALUES (@w, @s, @d, @t, @o, @r, @k)",
                    ("@w", a.Wcid), ("@s", a.ShellWcid), ("@d", a.Donor), ("@t", a.DonatedAt), ("@o", a.ObjDesc), ("@r", a.Room), ("@k", a.WakeOnApproach));

            foreach (var w in d.Rewards)
                Exec("INSERT INTO zoo_reward (player, wcid, monster, for_donation, given_at, egg_guid) VALUES (@p, @w, @m, @f, @t, @g)",
                    ("@p", w.Player), ("@w", w.Wcid), ("@m", w.Monster), ("@f", w.ForDonation), ("@t", w.GivenAt), ("@g", w.EggGuid));

            foreach (var b in d.Blocked)
                Exec("INSERT INTO zoo_blocked (wcid) VALUES (@w)", ("@w", b));

            tx.Commit();
        }

        private static void Save()
        {
            if (loadFailed)
            {
                log.Error("PetZoo - not saving: the zoo failed to load from the shard DB (saving now would wipe it).");
                return;
            }

            // snapshot under the lock (round-trip through JSON = cheap deep copy), write off-thread
            string snapshot;
            long version;
            lock (dataLock)
            {
                snapshot = JsonSerializer.Serialize(data);
                version = ++saveVersion;
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                lock (saveLock)
                {
                    if (version <= writtenVersion)
                        return;   // a newer save already went out
                    try
                    {
                        WriteToDb(JsonSerializer.Deserialize<Data>(snapshot));
                        writtenVersion = version;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"PetZoo - failed to save to the shard DB: {ex.Message}");
                    }
                }
            });
        }
    }

    /// <summary>
    /// A harmless zoo creature. Built on the egg's pet shell (so it has no monster AI, loot,
    /// generators or attacks) wearing the donated creature's look at its full size.
    /// </summary>
    public class ZooAnimal : Pet
    {
        private PetZoo.Animal record;
        private double nextWanderTime;
        private double moveStartTime;
        private double busyUntil;

        private const double WanderMinDelay = 6.0;
        private const double WanderMaxDelay = 20.0;
        private const double StuckSeconds = 30.0;
        public const float WakeRange = 8.0f;   // a player this close wakes a WakeOnApproach animal
        private bool risen;

        private static readonly MotionCommand[] Twitches =
            { MotionCommand.Twitch1, MotionCommand.Twitch2, MotionCommand.Twitch3, MotionCommand.Twitch4 };

        public ZooAnimal(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
        }

        public ZooAnimal(ACE.Entity.Models.Biota biota) : base(biota)
        {
        }

        public bool Init(PetZoo.Animal a, Weenie creature, PetZoo.Point spawn)
        {
            record = a;

            ApplyCreatureAppearance(creature);
            capturedObjDesc = DeserializeObjDesc(a.ObjDesc);

            Name = creature.GetProperty(PropertyString.Name) ?? Name;
            ObjScale = (float)(creature.GetProperty(PropertyFloat.DefaultScale) ?? 1.0);

            var lvl = creature.GetProperty(PropertyInt.Level);
            if (lvl != null) Level = lvl.Value;

            // Template (string 5) = the line under the name on a creature's ID panel (where an NPC's title shows)
            SetProperty(PropertyString.Template, $"Donated by {a.Donor}");

            // harmless: nobody can attack it, it never fights, it's usable for the twitch
            Attackable = false;
            TargetingTactic = TargetingTactic.None;
            SetMonsterState();
            IsPassivePet = true;
            ItemUseable = Usable.RemoteNeverWalk;

            Location = spawn.ToPosition();
            Location.LandblockId = new LandblockId(Location.GetCell());

            TimeToRot = -1;
            NoCorpse = true;

            if (!EnterWorld())
                return false;

            var now = Time.GetUnixTime();
            nextSlowTickTime = now;
            nextWanderTime = now + ThreadSafeRandom.Next(1.0f, (float)WanderMaxDelay);
            return true;
        }

        /// <summary>Called 1x/second from Pet.SlowTick instead of the owner-follow logic.
        /// Strolls to a random spot within the room's radius of one of its center points.</summary>
        public void ZooSlowTick(double now)
        {
            // dormant creatures (slithis etc.): their "standing up" is the switch out of peace stance,
            // the same thing that happens when they wake in the wild. Rise when a player is near,
            // sink back when everyone has left. While sunk they stay put.
            if (record != null && record.WakeOnApproach)
            {
                var near = PhysicsObj?.ObjMaint?.GetKnownPlayersValuesAsPlayer()
                    .Any(p => p != null && GetCylinderDistance(p) <= WakeRange) ?? false;

                if (near && !risen)
                {
                    if (IsMoving) { CancelMoveTo(); OnMovementStopped(); }
                    SetZooStance(GetCombatStance());
                    risen = true;
                    nextWanderTime = now + 3.0;   // let the rise animation finish
                    return;
                }
                if (!near && risen)
                {
                    if (IsMoving) { CancelMoveTo(); OnMovementStopped(); }
                    SetZooStance(MotionStance.NonCombat);
                    risen = false;
                    return;
                }
                if (!risen)
                    return;
            }

            if (IsMoving)
            {
                if (now - moveStartTime > StuckSeconds)
                {
                    CancelMoveTo();
                    StopWander(now);
                }
                return;
            }

            if (now < nextWanderTime || now < busyUntil)
                return;

            var room = PetZoo.GetRoom(record?.Room);
            if (room == null || room.Points.Count == 0)
            {
                nextWanderTime = now + WanderMaxDelay;
                return;
            }

            var center = room.Points[ThreadSafeRandom.Next(0, room.Points.Count - 1)].ToPosition();

            // random spot in a circle around the center (sqrt keeps it evenly spread)
            var angle = ThreadSafeRandom.Next(0.0f, (float)(Math.PI * 2));
            var dist = room.Radius * Math.Sqrt(ThreadSafeRandom.Next(0.0f, 1.0f));
            center.PositionX += (float)(Math.Cos(angle) * dist);
            center.PositionY += (float)(Math.Sin(angle) * dist);
            if (!center.Indoors)
                { center.SetLandblock(); center.SetLandCell(); }   // fix the outdoor landblock+cell if the offset crossed into the next one (keeps Z -- roof stays roof)

            WanderTo(center, now);
        }

        private void SetZooStance(MotionStance stance)
        {
            var motion = new Motion(stance, MotionCommand.Ready);
            CurrentMotionState = motion;
            EnqueueBroadcastMotion(motion);
        }

        private MotionStance CurrentStance => CurrentMotionState?.Stance ?? MotionStance.NonCombat;

        private void WanderTo(Position dest, double now)
        {
            if (MoveSpeed == 0.0f)
                UpdateMovementSpeed();

            var motion = new Motion(this, dest);
            motion.MoveToParameters.MovementParameters =
                MovementParams.CanWalk | MovementParams.CanRun | MovementParams.FailWalk |
                MovementParams.MoveTowards | MovementParams.UseSpheres | MovementParams.SetHoldKey |
                MovementParams.ModifyRawState | MovementParams.ModifyInterpretedState |
                MovementParams.CancelMoveTo | MovementParams.StopCompletely;
            motion.MoveToParameters.DistanceToObject = 0.5f;
            motion.MoveToParameters.WalkRunThreshold = 1000.0f;   // always walk -- it's a stroll
            motion.MoveToParameters.Speed = 1.0f;
            motion.RunRate = RunRate;

            EnqueueBroadcastMotion(motion);
            CurrentMotionState = motion;

            if (!PhysicsObj.IsMovingOrAnimating)
                PhysicsObj.UpdateTime = Physics.Common.PhysicsTimer.CurrentTime;

            PhysicsObj.MoveToPosition(new Physics.Common.Position(dest), new MovementParameters(motion));

            IsMoving = true;
            moveStartTime = now;
        }

        private void StopWander(double now)
        {
            OnMovementStopped();
            nextWanderTime = now + ThreadSafeRandom.Next((float)WanderMinDelay, (float)WanderMaxDelay);
        }

        public override void OnMoveComplete(WeenieError status)
        {
            StopWander(Time.GetUnixTime());
        }

        /// <summary>Double-click: stop, then play one random twitch the creature actually has.</summary>
        public void DoTwitch(Player player)
        {
            var now = Time.GetUnixTime();
            if (now < busyUntil)
                return;

            var available = Twitches.Where(t =>
                ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(MotionTableId, CurrentStance, t) > 0).ToList();
            if (available.Count == 0)
                return;

            if (IsMoving)
            {
                CancelMoveTo();
                OnMovementStopped();
            }

            var twitch = available[ThreadSafeRandom.Next(0, available.Count - 1)];
            var length = ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(MotionTableId, CurrentStance, twitch);

            EnqueueBroadcastMotion(new Motion(CurrentStance, twitch));

            busyUntil = now + length + 0.5;
            nextWanderTime = Math.Max(nextWanderTime, busyUntil + WanderMinDelay);
        }
    }
}
