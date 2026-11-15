using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.Mutations
{
    public class MutationFilter
    {
        public List<Mutation> Mutations = new List<Mutation>();

        // MutationFilter -> Mutation -> MutationOutcome -> EffectList -> Effect
        
        public bool TryMutate(WorldObject wo, int tier = 1, float qualityMod = 0.0f)
        {
            var mutated = false;

            foreach (var mutation in Mutations)
            {
                // tier gate roll, rolled per mutation so gated mutations are independent
                // (ie. missile defense and magic defense each have their own 10% chance, instead of both-or-neither)
                //
                // the gate passes when rng < chance, so a better qualityMod has to push rng DOWN.
                // previously this was Next(qualityMod, 1.0), which made every gate below qualityMod impossible
                // (a 0.2 / 0.25 LootQualityMod could never roll missile / magic defense) and made all
                // gated upgrades (variance, burden, missile / magic defense) rarer on better loot.
                // gates of 1 / 0 (tier selection) behave exactly as before.
                double rng;
                if (qualityMod >= 0)
                    rng = ThreadSafeRandom.Next(0.0f, Math.Max(1.0f - qualityMod, 0.0f));
                else
                    rng = ThreadSafeRandom.Next(Math.Min(-qualityMod, 1.0f), 1.0f);

                mutated |= mutation.TryMutate(wo, tier, rng, qualityMod);
            }

            return mutated;
        }
    }
}
