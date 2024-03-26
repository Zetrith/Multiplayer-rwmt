using Multiplayer.Common;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client
{
    static class MultiplayerData
    {
        private static Dictionary<string, bool> xmlMods = new();
        private static Dictionary<string, bool> translationMods = new();
        public static Dictionary<string, DefInfo> localDefInfos;

        public static RoundModeEnum modCtorRoundMode;
        public static RoundModeEnum staticCtorRoundMode;

        public static void PrecacheMods()
        {
            foreach (var mod in ModLister.AllInstalledMods)
            {
                IsXmlMod(mod);
                IsTranslationMod(mod);
            }
        }

        public static bool IsXmlMod(ModMetaData mod)
        {
            if (xmlMods.TryGetValue(mod.RootDir.FullName, out var xml))
                return xml;

            return xmlMods[mod.RootDir.FullName] = !ModHasAssemblies(mod);
        }

        public static bool IsTranslationMod(ModMetaData mod)
        {
            if (translationMods.TryGetValue(mod.RootDir.FullName, out var xml))
                return xml;

            // ModContentPack.InitLoadFolders can throw "Illegal characters in path"
            try
            {
                var dummyPack = DummyContentPack(mod);

                return translationMods[mod.RootDir.FullName] =
                    !ModHasAssemblies(mod)
                    && !ModContentPack.GetAllFilesForModPreserveOrder(dummyPack, "Defs/", _ => true).Any()
                    && !ModContentPack.GetAllFilesForModPreserveOrder(dummyPack, "Patches/", _ => true).Any()
                    && ModContentPack.GetAllFilesForModPreserveOrder(dummyPack, "Languages/", _ => true).Any();
            }
            catch (Exception e)
            {
                Log.Error($"Error getting information about mod {mod.RootDir.FullName}: {e}");
                return translationMods[mod.RootDir.FullName] = false;
            }
        }

        public static bool ModHasAssemblies(ModMetaData mod)
        {
            return Directory.EnumerateFiles(mod.RootDir.FullName, "*.dll", SearchOption.AllDirectories).Any();
        }

        public static IEnumerable<FileInfo> GetModAssemblies(ModContentPack mod)
        {
            return ModContentPack.GetAllFilesForModPreserveOrder(mod, "Assemblies/", f => f.ToLower() == ".dll")
                .Select(t => t.Item2);
        }

        public static ModContentPack DummyContentPack(ModMetaData mod)
        {
            // The constructor calls InitLoadFolders
            return new ModContentPack(mod.RootDir, mod.PackageId, mod.PackageIdPlayerFacing, 0, mod.Name, mod.Official);
        }

        public static UniqueList<Texture> icons = new();
        public static UniqueList<IconInfo> iconInfos = new();

        public class IconInfo
        {
            public bool hasStuff;
        }

        internal static void CollectCursorIcons()
        {
            icons.Add(null);
            iconInfos.Add(null);

            foreach (var des in DefDatabase<DesignationCategoryDef>.AllDefsListForReading.SelectMany(c => c.AllResolvedDesignators))
            {
                if (des.icon == null) continue;

                if (icons.Add(des.icon))
                    iconInfos.Add(new IconInfo()
                    {
                        hasStuff = des is Designator_Build build && build.entDef.MadeFromStuff
                    });
            }
        }

        internal static HashSet<Type> IgnoredVanillaDefTypes = new()
        {
            typeof(FeatureDef), typeof(HairDef),
            typeof(MainButtonDef), typeof(PawnTableDef),
            typeof(TransferableSorterDef), typeof(ConceptDef),
            typeof(InstructionDef), typeof(EffecterDef),
            typeof(ImpactSoundTypeDef), typeof(KeyBindingCategoryDef),
            typeof(KeyBindingDef), typeof(RulePackDef),
            typeof(ScatterableDef), typeof(ShaderTypeDef),
            typeof(SongDef), typeof(SoundDef),
            typeof(SubcameraDef), typeof(PawnColumnDef)
        };

        internal static void CollectDefInfos()
        {
            var dict = new Dictionary<string, DefInfo>();

            int TypeHash(Type type) => GenText.StableStringHash(type.FullName);

            dict["ThingComp"] = GetDefInfo(CompSerialization.thingCompTypes, TypeHash);
            dict["AbilityComp"] = GetDefInfo(CompSerialization.abilityCompTypes, TypeHash);
            dict["WorldObjectComp"] = GetDefInfo(CompSerialization.worldObjectCompTypes, TypeHash);
            dict["HediffComp"] = GetDefInfo(CompSerialization.hediffCompTypes, TypeHash);
            dict["IStoreSettingsParent"] = GetDefInfo(RwImplSerialization.storageParents, TypeHash);
            dict["IPlantToGrowSettable"] = GetDefInfo(RwImplSerialization.plantToGrowSettables, TypeHash);
            dict["Designator"] = GetDefInfo(RwImplSerialization.designatorTypes, TypeHash);
            dict["DefTypes"] = GetDefInfo(DefSerialization.DefTypes, TypeHash);

            dict["GameComponent"] = GetDefInfo(CompSerialization.gameCompTypes, TypeHash);
            dict["WorldComponent"] = GetDefInfo(CompSerialization.worldCompTypes, TypeHash);
            dict["MapComponent"] = GetDefInfo(CompSerialization.mapCompTypes, TypeHash);
            dict["ISyncSimple"] = GetDefInfo(ApiSerialization.syncSimples, TypeHash);
            dict["ISession"] = GetDefInfo(ApiSerialization.sessions, TypeHash);

            dict["PawnBio"] = GetDefInfo(SolidBioDatabase.allBios, b => b.name.GetHashCode());

            foreach (var defType in GenTypes.AllLeafSubclasses(typeof(Def)))
            {
                if (defType.Assembly != typeof(Game).Assembly) continue;
                if (IgnoredVanillaDefTypes.Contains(defType)) continue;

                var defs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType);
                dict.Add(defType.Name, GetDefInfo(defs, d => GenText.StableStringHash(d.defName)));
            }

            localDefInfos = dict;
        }

        internal static DefInfo GetDefInfo<T>(IEnumerable<T> types, Func<T, int> hash)
        {
            return new DefInfo()
            {
                count = types.Count(),
                hash = types.Select(t => hash(t)).AggregateHash()
            };
        }
    }
}
