using System;
using System.Collections.Concurrent;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Factories.Tables;
using ACE.Server.Managers;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Custom: beneficial "Other" buffs (Strength Other, Lightning Protection Other, etc.)
    /// get a per-spell-id casting cooldown based on the spell's duration.
    /// Stored in memory keyed by character guid, so it survives relog but resets on server restart.
    ///
    /// The cooldown only starts (or counts toward starting) when the cast actually landed an enchantment:
    /// fizzles, resists, movement-disrupted casts, invalid targets and (by default) casts that are
    /// surpassed by a stronger existing buff do not count.
    ///
    /// Configurable via /modifybool, /modifylong, /modifydouble:
    ///   other_buff_cooldown_enabled              (bool)   master switch
    ///   other_buff_cooldown_fellowship           (bool)   also apply to fellowship buffs
    ///   other_buff_cooldown_count_surpassed      (bool)   count casts that were surpassed / blocked
    ///   other_buff_cooldown_min_level            (long)   lowest spell level (1-8) the cooldown applies to; 1 = all levels
    ///   other_buff_cooldown_casts                (long)   successful casts of a spell before its cooldown triggers
    ///   other_buff_cooldown_cast_window          (long)   seconds before a partial cast count expires (0 = never)
    ///   other_buff_cooldown_max_seconds          (long)   hard cap on cooldown length
    ///   other_buff_cooldown_duration_multiplier  (double) cooldown = spell duration * multiplier
    /// </summary>
    partial class Player
    {
        private static readonly ConcurrentDictionary<(uint CharGuid, uint SpellId), DateTime> OtherBuffCooldowns = new ConcurrentDictionary<(uint, uint), DateTime>();

        private static readonly ConcurrentDictionary<(uint CharGuid, uint SpellId), (int Count, DateTime LastCast)> OtherBuffCastCounts = new ConcurrentDictionary<(uint, uint), (int, DateTime)>();

        // state for the cast currently in flight (set in BeginOtherBuffCast, resolved in EndOtherBuffCast)
        private uint otherBuffPendingSpellId;
        private double otherBuffPendingDuration;
        private bool otherBuffPendingLanded;

        private static bool OtherBuffCooldownEnabled => PropertyManager.GetBool("other_buff_cooldown_enabled").Item;

        /// <summary>
        /// Beneficial, has a duration (i.e. an enchantment, not Heal Other etc.), is not an Item Enchantment spell, and is either
        /// a fellowship spell or a non-self-targeted spell.
        /// </summary>
        public static bool IsOtherBuff(Spell spell)
        {
            if (spell == null || spell.NotFound)
                return false;

            if (!spell.IsBeneficial || spell.Duration <= 0)
                return false;

            // minimum spell level (1-8, as cast, i.e. before any specialization power-up) the cooldown applies to
            var minLevel = PropertyManager.GetLong("other_buff_cooldown_min_level").Item;
            if (minLevel > 1 && SpellLevelProgression.GetSpellLevel((SpellId)spell.Id) < minLevel)
                return false;

            // Item Enchantment spells can be cast on your own items, so they are not "Other" spells.
            // BaseSchool = the spell's real school, ignoring any specialization school override.
            if (spell.BaseSchool == MagicSchool.ItemEnchantment)
                return false;

            if (spell.Flags.HasFlag(SpellFlags.FellowshipSpell))
                return PropertyManager.GetBool("other_buff_cooldown_fellowship").Item;

            return !spell.IsSelfTargeted;
        }

        public bool IsOtherBuffOnCooldown(uint spellId, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;

            if (!OtherBuffCooldownEnabled)
                return false;

            if (!OtherBuffCooldowns.TryGetValue((Guid.Full, spellId), out var readyTime))
                return false;

            var now = DateTime.UtcNow;
            if (now >= readyTime)
            {
                OtherBuffCooldowns.TryRemove((Guid.Full, spellId), out _);
                return false;
            }

            remaining = readyTime - now;
            return true;
        }

        /// <summary>
        /// Called when a player's non-weapon spell passes the cast checks (no fizzle), BEFORE the specialization upgrade,
        /// so the cooldown is based on the spell as the player actually cast it.
        /// Nothing is committed until EndOtherBuffCast() confirms an enchantment landed.
        /// </summary>
        public void BeginOtherBuffCast(Spell spell)
        {
            otherBuffPendingLanded = false;

            if (!OtherBuffCooldownEnabled || !IsOtherBuff(spell))
            {
                otherBuffPendingSpellId = 0;
                return;
            }

            otherBuffPendingSpellId = spell.Id;
            otherBuffPendingDuration = spell.Duration;
        }

        /// <summary>
        /// Called from WorldObject.CreateEnchantment() once EnchantmentManager.Add() has returned.
        /// For fellowship casts this fires once per fellow; one landed buff is enough.
        /// </summary>
        public void OnOtherBuffEnchantmentAdded(Spell spell, AddEnchantmentResult addResult)
        {
            if (otherBuffPendingSpellId == 0 || spell == null || spell.Id != otherBuffPendingSpellId)
                return;

            if (addResult == null || addResult.Enchantment == null)
                return;     // blocked outright (unsurpassable etc.): nothing was applied

            if ((addResult.StackType == StackType.Surpassed || addResult.StackType == StackType.None)
                && !PropertyManager.GetBool("other_buff_cooldown_count_surpassed").Item)
                return;     // buff was stored but a stronger one is active, so it affected nothing

            otherBuffPendingLanded = true;
        }

        /// <summary>
        /// Commits the cooldown / cast count only if the cast landed something.
        /// </summary>
        public void EndOtherBuffCast()
        {
            var spellId = otherBuffPendingSpellId;
            var duration = otherBuffPendingDuration;
            var landed = otherBuffPendingLanded;

            otherBuffPendingSpellId = 0;
            otherBuffPendingLanded = false;

            if (spellId == 0 || !landed)
                return;

            RegisterOtherBuffCast(spellId, duration);
        }

        private void RegisterOtherBuffCast(uint spellId, double spellDuration)
        {
            var key = (Guid.Full, spellId);
            var now = DateTime.UtcNow;

            var castsRequired = Math.Max(1, PropertyManager.GetLong("other_buff_cooldown_casts").Item);

            if (castsRequired > 1)
            {
                var window = PropertyManager.GetLong("other_buff_cooldown_cast_window").Item;

                var count = 1;
                if (OtherBuffCastCounts.TryGetValue(key, out var prev) && prev.Count > 0
                    && (window <= 0 || (now - prev.LastCast).TotalSeconds <= window))
                    count = prev.Count + 1;

                if (count < castsRequired)
                {
                    OtherBuffCastCounts[key] = (count, now);
                    return;
                }

                OtherBuffCastCounts.TryRemove(key, out _);
            }

            var multiplier = PropertyManager.GetDouble("other_buff_cooldown_duration_multiplier").Item;
            var maxSeconds = PropertyManager.GetLong("other_buff_cooldown_max_seconds").Item;

            var seconds = spellDuration * multiplier;
            if (maxSeconds > 0)
                seconds = Math.Min(seconds, maxSeconds);

            if (seconds <= 0)
                return;

            OtherBuffCooldowns[key] = now.AddSeconds(seconds);
        }

        public static string FormatOtherBuffCooldown(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}h {t.Minutes}m";
            if (t.TotalMinutes >= 1)
                return $"{t.Minutes}m {t.Seconds}s";
            return $"{Math.Max(1, t.Seconds)}s";
        }
    }
}
