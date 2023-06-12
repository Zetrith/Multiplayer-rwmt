﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using Multiplayer.Common;
using Prepatcher;
using RimWorld;

namespace Multiplayer.Client;

public static class Prepatches
{
    [FreePatch]
    static void DontSetResolution(ModuleDefinition module)
    {
        if (MpVersion.IsDebug)
        {
            var utilType = module.ImportReference(typeof(ResolutionUtility)).Resolve();
            utilType.FindMethod("SetResolutionRaw").Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ret));
        }
    }
}
