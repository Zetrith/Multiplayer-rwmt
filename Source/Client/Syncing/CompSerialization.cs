﻿using System;
using Multiplayer.Client.Util;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client;

public static class CompSerialization
{
    public static Type[] gameCompTypes;
    public static Type[] worldCompTypes;
    public static Type[] mapCompTypes;

    public static Type[] thingCompTypes;
    public static Type[] hediffCompTypes;
    public static Type[] abilityCompTypes;
    public static Type[] worldObjectCompTypes;

    public static void Init()
    {
        thingCompTypes = TypeUtil.AllSubclassesNonAbstractOrdered(typeof(ThingComp));
        hediffCompTypes = TypeUtil.AllSubclassesNonAbstractOrdered(typeof(HediffComp));
        abilityCompTypes = TypeUtil.AllSubclassesNonAbstractOrdered(typeof(AbilityComp));
        worldObjectCompTypes = TypeUtil.AllSubclassesNonAbstractOrdered(typeof(WorldObjectComp));

        gameCompTypes = TypeUtil.AllSubclassesNonAbstractOrdered(typeof(GameComponent));
        worldCompTypes = TypeUtil.AllSubclassesNonAbstractOrdered(typeof(WorldComponent));
        mapCompTypes = TypeUtil.AllSubclassesNonAbstractOrdered(typeof(MapComponent));
    }
}
