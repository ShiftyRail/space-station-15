﻿using System;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Jittering;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Server.Chemistry.ReagentEffects.StatusEffects
{
    /// <summary>
    ///     Adds the jitter status effect to a mob.
    ///     This doesn't use generic status effects because it needs to
    ///     take in some parameters that JitterSystem needs.
    /// </summary>
    public class Jitter : ReagentEffect
    {
        [DataField("amplitude")]
        public float Amplitude = 10.0f;

        [DataField("frequency")]
        public float Frequency = 4.0f;

        [DataField("time")]
        public float Time = 2.0f;

        public override void Effect(ReagentEffectArgs args)
        {
            args.EntityManager.EntitySysManager.GetEntitySystem<SharedJitteringSystem>()
                .DoJitter(args.SolutionEntity, TimeSpan.FromSeconds(Time), Amplitude, Frequency);
        }
    }
}
