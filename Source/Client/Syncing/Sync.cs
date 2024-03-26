using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Client.Persistent;
using Verse;

namespace Multiplayer.Client
{
    public static class Sync
    {
        public static List<SyncHandler> handlers = new();
        public static List<SyncField> bufferedFields = new();

        // Internal maps for Harmony patches
        public static Dictionary<MethodBase, int> methodBaseToInternalId = new();
        public static List<ISyncCall> internalIdToSyncMethod = new();

        static Dictionary<string, SyncField> registeredSyncFields = new();

        public static void PostInitHandlers()
        {
            handlers.SortStable((a, b) => a.version.CompareTo(b.version));

            for (int i = 0; i < handlers.Count; i++)
                handlers[i].syncId = i;
        }

        public static SyncMethod Method(Type targetType, string methodName, SyncType[] argTypes = null)
        {
            return Method(targetType, null, methodName, argTypes);
        }

        public static SyncMethod Method(Type targetType, string instancePath, string methodName, SyncType[] argTypes = null)
        {
            var instanceType = instancePath == null ? targetType : MpReflection.PathType($"{targetType}/{instancePath}");
            var method = AccessTools.Method(instanceType, methodName, argTypes?.Select(t => t.type).ToArray())
                ?? throw new Exception($"Couldn't find method {instanceType}::{methodName}");

            SyncMethod handler = new SyncMethod(targetType, instancePath, method, argTypes);
            handlers.Add(handler);
            return handler;
        }

        public static SyncField Field(Type targetType, string fieldName)
        {
            return Field(targetType, null, fieldName);
        }

        public static SyncField Field(Type targetType, string instancePath, string fieldName)
        {
            SyncField handler = new SyncField(targetType, instancePath + "/" + fieldName);
            handlers.Add(handler);
            return handler;
        }

        public static SyncField[] Fields(Type targetType, string instancePath, params string[] memberPaths)
        {
            return memberPaths.Select(path => Field(targetType, instancePath, path)).ToArray();
        }

        public static ISyncField RegisterSyncField(Type targetType, string fieldName)
        {
            return RegisterSyncField(AccessTools.Field(targetType, fieldName));
        }

        public static ISyncField RegisterSyncField(FieldInfo field)
        {
            string memberPath = field.ReflectedType + "/" + field.Name;
            SyncField sf;
            if (field.IsStatic) {
                sf = Field(null, null, memberPath);
            } else {
                sf = Field(field.ReflectedType, null, field.Name);
            }

            registeredSyncFields.Add(memberPath, sf);

            return sf;
        }

        public static SyncDelegate RegisterSyncDelegate(Type inType, string nestedType, string methodName, string[] fields = null, Type[] args = null)
        {
            string typeName = $"{inType}+{nestedType}";
            Type delegateType = MpReflection.GetTypeByName(typeName);
            if (delegateType == null)
                throw new Exception($"Couldn't find type {typeName}");

            MethodInfo method = AccessTools.Method(delegateType, methodName, args);
            if (method == null)
                throw new Exception($"Couldn't find method {typeName}::{methodName}");

            return RegisterSyncDelegate(method, fields);
        }

        public static SyncDelegate RegisterSyncDelegate(MethodInfo method, string[] fields)
        {
            SyncDelegate handler = new SyncDelegate(method.DeclaringType, method, fields);
            methodBaseToInternalId[handler.method] = internalIdToSyncMethod.Count;
            internalIdToSyncMethod.Add(handler);
            handlers.Add(handler);

            SyncUtil.PatchMethodForSync(method);

            return handler;
        }

        public static SyncMethod RegisterSyncMethod(Type type, string methodOrPropertyName, SyncType[] argTypes = null)
        {
            MethodInfo method = AccessTools.Method(type, methodOrPropertyName, argTypes?.Select(t => t.type).ToArray());

            if (method == null) {
                PropertyInfo property = AccessTools.Property(type, methodOrPropertyName);

                if (property != null) {
                    method = property.GetSetMethod();
                }
            }

            if (method == null)
                throw new Exception($"Couldn't find method or property {methodOrPropertyName} in type {type}");

            return RegisterSyncMethod(method, argTypes);
        }

        /// <summary>
        /// Registers all declared SyncMethod and SyncField attributes in the assembly
        /// </summary>
        internal static void RegisterAllAttributes(Assembly asm)
        {
            foreach (Type type in asm.GetTypes())
            {
                foreach (MethodInfo method in type.GetDeclaredMethods())
                {
                    try
                    {
                        if (method.TryGetAttribute(out SyncMethodAttribute sma))
                            RegisterSyncMethod(method, sma);
                        else if (method.TryGetAttribute(out SyncWorkerAttribute swa))
                            RegisterSyncWorker(method, isImplicit: swa.isImplicit, shouldConstruct: swa.shouldConstruct);
                        else if (method.TryGetAttribute(out SyncDialogNodeTreeAttribute _))
                            RegisterSyncDialogNodeTree(method);
                        else if (method.TryGetAttribute(out PauseLockAttribute _))
                            RegisterPauseLock(method);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Exception registering SyncMethod {type}::{method} by attribute: {e}");
                        Multiplayer.loadingErrors = true;
                    }
                }

                foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
                {
                    try
                    {
                        if (field.TryGetAttribute(out SyncFieldAttribute sfa))
                            RegisterSyncField(field, sfa);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Exception registering SyncField by attribute: {e}");
                        Multiplayer.loadingErrors = true;
                    }
                }
            }
        }

        private static void RegisterSyncMethod(MethodInfo method, SyncMethodAttribute attribute)
        {
            int[] exposeParameters = attribute.exposeParameters;
            int paramNum = method.GetParameters().Length;

            if (exposeParameters != null && exposeParameters.Any(p => p < 0 || p >= paramNum))
            {
                Log.Error($"Failed to register a method: One or more indexes of parameters to expose in SyncMethod attribute applied to {method.DeclaringType?.FullName}::{method} is invalid.");
                return;
            }

            var sm = RegisterSyncMethod(method);
            sm.context = attribute.context;
            sm.debugOnly = attribute.debugOnly;

            sm.SetContext(attribute.context);

            if (attribute.cancelIfAnyArgNull)
                sm.CancelIfAnyArgNull();

            if (attribute.cancelIfNoSelectedMapObjects)
                sm.CancelIfNoSelectedMapObjects();

            if (attribute.cancelIfNoSelectedWorldObjects)
                sm.CancelIfNoSelectedWorldObjects();

            if (attribute.debugOnly)
                sm.SetDebugOnly();

            if (exposeParameters != null) {
                int i = 0;

                try {
                    for (; i < exposeParameters.Length; i++) {
                        sm.ExposeParameter(exposeParameters[i]);
                    }
                } catch (Exception exc) {
                    Log.Error($"An exception occurred while exposing parameter {i} ({method.GetParameters()[i]}) for method {method.DeclaringType?.FullName}::{method}: {exc}");
                }
            }
        }

        public static SyncField GetRegisteredSyncField(Type target, string name)
        {
            return GetRegisteredSyncField(target + "/" + name);
        }

        public static SyncField GetRegisteredSyncField(string memberPath)
        {
            if (registeredSyncFields.TryGetValue(memberPath, out SyncField cached))
                return cached;

            var syncField = handlers.OfType<SyncField>().FirstOrDefault(sf => sf.memberPath == memberPath);

            registeredSyncFields[memberPath] = syncField;
            return syncField;
        }

        static void RegisterSyncField(FieldInfo field, SyncFieldAttribute attribute)
        {
            SyncField sf = Field(field.ReflectedType, field.Name);

            registeredSyncFields.Add(field.ReflectedType + "/" + field.Name, sf);

            if (MpVersion.IsDebug) {
                Log.Message($"Registered Field: {field.ReflectedType}/{field.Name}");
            }

            if (attribute.cancelIfValueNull)
                sf.CancelIfValueNull();

            if (attribute.inGameLoop)
                sf.InGameLoop();

            if (attribute.bufferChanges)
                sf.SetBufferChanges();

            if (attribute.debugOnly)
                sf.SetDebugOnly();

            if (attribute.hostOnly)
                sf.SetHostOnly();

            if (attribute.version > 0)
                sf.SetVersion(attribute.version);
        }

        public static SyncMethod RegisterSyncMethod(MethodInfo method, SyncType[] argTypes = null)
        {
            MpUtil.MarkNoInlining(method);

            SyncMethod handler = new SyncMethod(method.IsStatic ? null : method.DeclaringType, null, method, argTypes);
            methodBaseToInternalId[handler.method] = internalIdToSyncMethod.Count;
            internalIdToSyncMethod.Add(handler);
            handlers.Add(handler);

            SyncUtil.PatchMethodForSync(method);

            return handler;
        }

        public static void RegisterSyncWorker(MethodInfo method, Type targetType = null, bool isImplicit = false, bool shouldConstruct = false)
        {
            Type[] parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();

            if (!method.IsStatic) {
                Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker method has to be static.");
                return;
            }

            if (parameters.Length != 2) {
                Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker method has an invalid number of parameters.");
                return;
            }

            if (parameters[0] != typeof(SyncWorker)) {
                Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker method has an invalid first parameter (got {parameters[0]}, expected ISyncWorker).");
                return;
            }

            if (targetType != null && parameters[1].IsAssignableFrom(targetType)) {
                Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker method has an invalid second parameter (got {parameters[1]}, expected {targetType} or assignable).");
                return;
            }

            if (!parameters[1].IsByRef) {
                Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker method has an invalid second parameter, should be a ref.");
                return;
            }

            var type = targetType ?? parameters[1].GetElementType();

            if (isImplicit) {
                if (method.ReturnType != typeof(bool)) {
                    Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker set as implicit (or the argument type is an interface) requires bool type as a return value.");
                    return;
                }
            } else if (method.ReturnType != typeof(void)) {
                Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker set as explicit should have void as a return value.");
                return;
            }

            SyncWorkerEntry entry = SyncDict.syncWorkers.GetOrAddEntry(type, isImplicit: isImplicit, shouldConstruct: shouldConstruct);
            entry.Add(method);

            if (!(isImplicit || type.IsInterface) && entry.SyncWorkerCount > 1) {
                Log.Warning($"Warning in {method.DeclaringType?.FullName}::{method}: type {type} has already registered an explicit SyncWorker, the code in this method may be not used.");
            }

            Log.Message($"Registered a SyncWorker {method.DeclaringType?.FullName}::{method} for type {type} in assembly {method.DeclaringType?.Assembly.GetName().Name}");
        }

        public static void RegisterSyncWorker<T>(SyncWorkerDelegate<T> syncWorkerDelegate, Type targetType = null, bool isImplicit = false, bool shouldConstruct = false)
        {
            MethodInfo method = syncWorkerDelegate.Method;
            Type[] parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();

            if (targetType != null && parameters[1].IsAssignableFrom(targetType)) {
                Log.Error($"Error in {method.DeclaringType?.FullName}::{method}: SyncWorker method has an invalid second parameter (got {parameters[1]}, expected {targetType} or assignable).");
                return;
            }

            var type = targetType ?? typeof(T);
            SyncWorkerEntry entry = SyncDict.syncWorkers.GetOrAddEntry(type, isImplicit: isImplicit, shouldConstruct: shouldConstruct);
            entry.Add(syncWorkerDelegate);

            if (!(isImplicit || type.IsInterface) && entry.SyncWorkerCount > 1) {
                Log.Warning($"Warning in {method.DeclaringType?.FullName}::{method}: type {type} has already registered an explicit SyncWorker, the code in this method may be not used.");
            }
        }

        public static void RegisterSyncDialogNodeTree(Type type, string methodOrPropertyName, SyncType[] argTypes = null)
        {
            MethodInfo method = AccessTools.Method(type, methodOrPropertyName, argTypes?.Select(t => t.type).ToArray());

            if (method == null)
            {
                PropertyInfo property = AccessTools.Property(type, methodOrPropertyName);

                if (property != null)
                {
                    method = property.GetSetMethod();
                }
            }

            if (method == null)
                throw new Exception($"Couldn't find method or property {methodOrPropertyName} in type {type}");

            RegisterSyncDialogNodeTree(method);
        }

        public static void RegisterSyncDialogNodeTree(MethodInfo method)
        {
            SyncUtil.PatchMethodForDialogNodeTreeSync(method);
        }

        public static void RegisterPauseLock(MethodInfo method)
        {
            var pauseLock = AccessTools.MethodDelegate<PauseLockDelegate>(method);

            if (pauseLock == null)
                throw new Exception($"Couldn't generate pause lock delegate from {method.DeclaringType?.FullName}:{method.Name}");

            RegisterPauseLock(pauseLock);
        }

        public static void RegisterPauseLock(PauseLockDelegate pauseLock)
        {
            if (PauseLockSession.pauseLocks.Contains(pauseLock))
                throw new Exception($"Pause lock already registered: {pauseLock}");

            PauseLockSession.pauseLocks.Add(pauseLock);
        }

        public static void ValidateAll()
        {
            foreach (var handler in handlers)
            {
                try
                {
                    handler.Validate();
                } catch (Exception e)
                {
                    Log.Error($"{handler} validation failed: {e}");
                    Multiplayer.loadingErrors = true;
                }
            }
        }
    }

    public static class GroupExtensions
    {
        public static void Watch(this SyncField[] group, object target = null, int index = -1)
        {
            foreach (SyncField field in group)
                if (field.targetType == null || field.targetType.IsInstanceOfType(target))
                    field.Watch(target, index);
        }

        public static SyncField[] SetBufferChanges(this SyncField[] group)
        {
            foreach (SyncField field in group)
                field.SetBufferChanges();
            return group;
        }

        public static SyncField[] PostApply(this SyncField[] group, Action<object, object> func)
        {
            foreach (SyncField field in group)
                field.PostApply(func);
            return group;
        }
    }
}
