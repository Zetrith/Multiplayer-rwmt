﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Common;
using Verse;

namespace Multiplayer.Client;

public static class RwSerialization
{
    public static void Init()
    {
        // CanHandle hooks
        SyncSerialization.canHandleHooks.Add(syncType =>
        {
            var type = syncType.type;
            if (type.IsGenericType && type.GetGenericTypeDefinition() is { } gtd)
                if (gtd == typeof(Pair<,>))
                    return SyncSerialization.CanHandleGenericArgs(type);

            if (syncType.expose)
                return typeof(IExposable).IsAssignableFrom(type);
            if (type == typeof(ISyncSimple))
                return true;
            if (typeof(ISyncSimple).IsAssignableFrom(type))
                return ApiSerialization.syncSimples.
                    Where(t => type.IsAssignableFrom(t)).
                    SelectMany(AccessTools.GetDeclaredFields).
                    All(f => SyncSerialization.CanHandle(f.FieldType));
            if (typeof(Def).IsAssignableFrom(type))
                return true;
            if (typeof(Designator).IsAssignableFrom(type))
                return true;

            return SyncDict.syncWorkers.TryGetValue(type, out _);
        });

        // Verse.Pair<,> serialization
        SyncSerialization.AddSerializationHook(
            syncType => syncType.type.IsGenericType && syncType.type.GetGenericTypeDefinition() is { } gtd && gtd == typeof(Pair<,>),
            (data, syncType) =>
            {
                Type[] arguments = syncType.type.GetGenericArguments();
                object[] parameters =
                {
                    SyncSerialization.ReadSyncObject(data, arguments[0]),
                    SyncSerialization.ReadSyncObject(data, arguments[1]),
                };
                return syncType.type.GetConstructors().First().Invoke(parameters);
            },
            (data, obj, syncType) =>
            {
                var type = syncType.type;
                Type[] arguments = type.GetGenericArguments();

                SyncSerialization.WriteSyncObject(data, AccessTools.DeclaredField(type, "first").GetValue(obj), arguments[0]);
                SyncSerialization.WriteSyncObject(data, AccessTools.DeclaredField(type, "second").GetValue(obj), arguments[1]);
            }
        );

        // IExposable serialization
        SyncSerialization.AddSerializationHook(
            syncType => syncType.expose,
            (data, syncType) =>
            {
                if (!typeof(IExposable).IsAssignableFrom(syncType.type))
                    throw new SerializationException($"Type {syncType.type} can't be exposed because it isn't IExposable");

                byte[] exposableData = data.ReadPrefixedBytes();
                return ExposableSerialization.ReadExposable(syncType.type, exposableData);
            },
            (data, obj, syncType) =>
            {
                if (!typeof(IExposable).IsAssignableFrom(syncType.type))
                    throw new SerializationException($"Type {syncType} can't be exposed because it isn't IExposable");

                var log = (data as LoggingByteWriter)?.Log;
                IExposable exposable = obj as IExposable;
                byte[] xmlData = ScribeUtil.WriteExposable(exposable);
                LogXML(log, xmlData);
                data.WritePrefixedBytes(xmlData);
            }
        );

        // ISyncSimple serialization
        // todo null handling for ISyncSimple?
        SyncSerialization.AddSerializationHook(
            syncType => typeof(ISyncSimple).IsAssignableFrom(syncType.type),
            (data, _) =>
            {
                ushort typeIndex = data.ReadUShort();
                var objType = ApiSerialization.syncSimples[typeIndex];
                var obj = MpUtil.NewObjectNoCtor(objType);
                foreach (var field in AccessTools.GetDeclaredFields(objType))
                    field.SetValue(obj, SyncSerialization.ReadSyncObject(data, field.FieldType));
                return obj;
            },
            (data, obj, _) =>
            {
                data.WriteUShort((ushort)ApiSerialization.syncSimples.FindIndex(obj!.GetType()));
                foreach (var field in AccessTools.GetDeclaredFields(obj.GetType()))
                    SyncSerialization.WriteSyncObject(data, field.GetValue(obj), field.FieldType);
            }
        );

        // Def serialization
        SyncSerialization.AddSerializationHook(
            syncType => typeof(Def).IsAssignableFrom(syncType.type),
            (data, _) =>
            {
                ushort defTypeIndex = data.ReadUShort();
                if (defTypeIndex == ushort.MaxValue)
                    return null;

                ushort shortHash = data.ReadUShort();

                var defType = DefSerialization.DefTypes[defTypeIndex];
                var def = DefSerialization.GetDef(defType, shortHash);

                if (def == null)
                    throw new SerializationException($"Couldn't find {defType} with short hash {shortHash}");

                return def;
            },
            (data, obj, _) =>
            {
                if (obj is not Def def)
                {
                    data.WriteUShort(ushort.MaxValue);
                    return;
                }

                var defTypeIndex = Array.IndexOf(DefSerialization.DefTypes, def.GetType());
                if (defTypeIndex == -1)
                    throw new SerializationException($"Unknown def type {def.GetType()}");

                data.WriteUShort((ushort)defTypeIndex);
                data.WriteUShort(def.shortHash);
            }
        );

        // Designator type changer
        // todo handle null?
        SyncSerialization.AddTypeChanger(
            syncType => typeof(Designator).IsAssignableFrom(syncType.type),
            (data, _) =>
            {
                ushort desId = SyncSerialization.ReadSync<ushort>(data);
                return RwImplSerialization.designatorTypes[desId];
            },
            (data, obj, _) =>
            {
                data.WriteUShort((ushort) Array.IndexOf(RwImplSerialization.designatorTypes, obj!.GetType()));
            }
        );

        RwImplSerialization.Init();
        CompSerialization.Init();
        ApiSerialization.Init();
        DefSerialization.Init();

        RwTypeHelper.Init();
        SyncWorkerTypeHelper.GetType = RwTypeHelper.GetType;
        SyncWorkerTypeHelper.GetTypeIndex = RwTypeHelper.GetTypeIndex;
    }

    internal static T GetAnyParent<T>(Thing thing) where T : class
    {
        if (thing is T t)
            return t;

        for (var parentHolder = thing.ParentHolder; parentHolder != null; parentHolder = parentHolder.ParentHolder)
            if (parentHolder is T t2)
                return t2;

        return null;
    }

    internal static string ThingHolderString(Thing thing)
    {
        StringBuilder builder = new StringBuilder(thing.ToString());

        for (var parentHolder = thing.ParentHolder; parentHolder != null; parentHolder = parentHolder.ParentHolder)
        {
            builder.Insert(0, "=>");
            builder.Insert(0, parentHolder.ToString());
        }

        return builder.ToString();
    }

    private static void LogXML(SyncLogger log, byte[] xmlData)
    {
        if (log == null) return;

        var reader = XmlReader.Create(new MemoryStream(xmlData));

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                string name = reader.Name;
                if (reader.GetAttribute("IsNull") == "True")
                    name += " (IsNull)";

                if (reader.IsEmptyElement)
                    log.Node(name);
                else
                    log.Enter(name);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                log.Exit();
            }
            else if (reader.NodeType == XmlNodeType.Text)
            {
                log.AppendToCurrentName($": {reader.Value}");
            }
        }
    }
}
