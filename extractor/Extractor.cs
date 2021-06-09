using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace extractor
{
    public static class Extractor
    {
        private static readonly Dictionary<Type, TypeInfo> typeDict = new Dictionary<Type, TypeInfo>();

        private static readonly List<Type> abstractTypes = new List<Type>();

        public static Dictionary<Type, TypeInfo> parse(IEnumerable<Assembly> assemblies, string[] extraTypes)
        {
            var assems = assemblies.ToList();
            var RWAssem = assems.FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
            var UnityAssem = assems.FirstOrDefault(assembly => assembly.GetName().Name == "UnityEngine");
            var usedExtraTypes = new Dictionary<string, bool>();
            if (RWAssem != null && UnityAssem != null)
            {
                RWTypes.assembly = RWAssem;
                RWTypes.Def = RWAssem.GetType("Verse.Def");
                RWTypes.UnsavedAttribute = RWAssem.GetType("Verse.UnsavedAttribute");
                RWTypes.IntRange = RWAssem.GetType("Verse.IntRange");
                RWTypes.FloatRange = RWAssem.GetType("Verse.FloatRange");
                RWTypes.CompProperties = RWAssem.GetType("Verse.CompProperties");
                RWTypes.DescriptionAttribute = RWAssem.GetType("Verse.DescriptionAttribute");
                RWTypes.DefHyperlink = RWAssem.GetType("Verse.DefHyperlink");
                RWTypes.Entity = RWAssem.GetType("Verse.Entity");
                RWTypes.ISlateRef = RWAssem.GetType("RimWorld.QuestGen.ISlateRef");
                UnityEngineTypes.assembly = UnityAssem;
                UnityEngineTypes.Color = UnityAssem.GetType("UnityEngine.Color");

                foreach (var assembly in assems)
                {
                    Log.Info($"Extracting data from {assembly.GetName().FullName}");

                    try
                    {
                        // collect Def or CompProperties (naming convention)
                        var types = from type in assembly.GetTypes()
                            where type != null &&
                                  (type.IsSubclassOf(RWTypes.Def) || type.Name.Contains("CompProperties") ||
                                   type == RWTypes.Def || type == RWTypes.CompProperties ||
                                   type.IsSubclassOf(RWTypes.CompProperties) ||
                                   type.Name.Contains("ModMetaDataInternal") || type.Name.Contains("ModLoadFolders") ||
                                   extraTypes != null && extraTypes.Any(et =>
                                   {
                                       if (type.Name.Contains(et) && !usedExtraTypes.ContainsKey(et))
                                           usedExtraTypes.Add(et, true);
                                       return type.Name.Contains(et);
                                   }))
                            select type;

                        foreach (var et in extraTypes.Where(et => !(usedExtraTypes.TryGetValue(et, out var res) && res))
                        ) Log.Warn($"ExtraType {et} not found.");

                        CollectData_BFS(types, assems);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error was thrown while extracting data from {assembly.GetName().FullName}");
                        Log.Error(ex.Message);
                    }
                }

                Log.Info("Extracting related types");
                var relatedTypes = SearchDerivedClasses(assems).ToList();
                Log.Info($"Found {relatedTypes.Count} related types");
                CollectData_BFS(relatedTypes, assems);
                PopulateData();

                return typeDict;
            }

            throw new Exception("Rimworld dll was not provided.");
        }

        private static void CollectData_BFS(IEnumerable<Type> _types, List<Assembly> assems)
        {
            var genericsToIgnore = new[]
            {
                typeof(List<>), typeof(Dictionary<string, string>), typeof(Nullable<>), typeof(HashSet<>),
                typeof(Stack<>), typeof(WeakReference<>)
            }.Select(t => t.GetGenericTypeDefinition()).ToList();
            var doNotCollectChildrenOf = new List<Type>
            {
                RWTypes.DefHyperlink
            };
            var types = new Queue<Type>(_types);
            while (types.Count > 0)
            {
                var type = types.Dequeue();

                TypeInfo typeInfo;
                if (!typeDict.TryGetValue(type, out typeInfo))
                {
                    typeInfo = TypeInfo.Create(type);
                    typeDict.Add(type, typeInfo);
                }

                if (type.IsSubclassOf(RWTypes.Entity) || type == RWTypes.Entity || type.Name.Contains("Map") ||
                    type.Name.Contains("Faction")) typeInfo.childCollected = true;

                if (doNotCollectChildrenOf.Contains(type) || type.IsGenericType &&
                    doNotCollectChildrenOf.Contains(type.GetGenericTypeDefinition())) typeInfo.childCollected = true;


                if (type.IsPrimitive || type == typeof(string))
                    continue;

                var parent = type.BaseType;
                if (parent != null && parent != typeof(object))
                {
                    typeInfo.specialType.parent = Util.GetTypeIdentifier(parent);
                    if (assems.Contains(parent.Assembly) && !typeDict.ContainsKey(parent))
                    {
                        Log.Info($"Adding {typeInfo.specialType.parent} which is parent of {typeInfo.typeIdentifier}");
                        types.Enqueue(parent);
                    }
                }
                else
                {
                    Log.Debug($"Not adding parent {parent} of {typeInfo.typeIdentifier}");
                }

                if (typeInfo.childCollected)
                    continue; // already collected

                if (type.IsAbstract && assems.Contains(type.Assembly)) abstractTypes.Add(type);

                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance /* | BindingFlags.NonPublic */);
                foreach (var field in fields)
                {
                    var fieldType = field.FieldType;
                    var fieldName = field.Name;

                    var unsavedAttr =
                        field.CustomAttributes.FirstOrDefault(attr => attr.AttributeType == RWTypes.UnsavedAttribute);
                    if (unsavedAttr != null)
                    {
                        var allowLoading = (bool) unsavedAttr.ConstructorArguments[0].Value;
                        if (allowLoading == false)
                        {
                            Log.Info($"Not adding {fieldName} as it is Unsaved");
                            continue;
                        }
                    }

                    if (fieldName.ToLower().EndsWith("int") || fieldName.ToLower().EndsWith("cache"))
                    {
                        Log.Info($"Not adding {fieldName} as it is internal");
                        continue;
                    }

                    /*
                    if (field.TryGetAttribute<UnsavedAttribute>(out var unsavedAttr))
                        if (!unsavedAttr.allowLoading)
                            continue;
                    */

                    if (!typeDict.ContainsKey(fieldType)) // if it is not registered
                    {
                        if (fieldType.IsGenericType)
                        {
                            if (genericsToIgnore.Contains(fieldType.GetGenericTypeDefinition()))
                            {
                                var id = fieldType.GetGenericTypeDefinition() == genericsToIgnore[0]
                                    ? Util.GetListTypeIdentifier(fieldType)
                                    : Util.GetGenericTypeIdentifier(fieldType); // don't need to fill child nodes.
                                Log.Info($"Not adding {id} because it is an ignored generic");
                                typeDict.Add(fieldType, TypeInfo.Create(id));
                            }
                            else
                            {
                                Log.Info(
                                    $"Adding {Util.GetGenericTypeIdentifier(fieldType)} which is field {fieldName} of {typeInfo.typeIdentifier}");
                                types.Enqueue(fieldType); // need to fill child nodes
                            }

                            if (RWTypes.ISlateRef.IsAssignableFrom(fieldType))
                                Log.Info($"Skipping {fieldName} because it's a SlateRef");
                            else
                                foreach (var T in fieldType.GetGenericArguments())
                                {
                                    if (T.IsGenericParameter) // example) K of List<K>, we don't need that
                                        continue;

                                    if (typeDict.ContainsKey(T)) continue;

                                    Log.Info(
                                        $"Adding {Util.GetTypeIdentifier(T)} which is generic argument of field {fieldName} of {typeInfo.typeIdentifier}");
                                    types.Enqueue(T);
                                    // typeDict.Add(T, TypeInfo.Create(T));
                                }
                        }
                        else
                        {
                            Log.Info(
                                $"Adding {Util.GetTypeIdentifier(fieldType)} which is field {fieldName} of {typeInfo.typeIdentifier}");
                            types.Enqueue(fieldType);
                        }
                    }

                    // set child type's typeId
                    if (fieldType.IsGenericType)
                    {
                        var identifier = string.Empty;
                        if (fieldType.GetGenericTypeDefinition() == genericsToIgnore[0])
                            identifier = Util.GetListTypeIdentifier(fieldType);
                        else
                            identifier = Util.GetGenericTypeIdentifier(fieldType);
                        typeInfo.childNodes[fieldName] = identifier;
                    }
                    else
                    {
                        // typeInfo.childNodes[fieldName] = $"{fieldType.Namespace}.{fieldType.Name}";
                        typeInfo.childNodes[fieldName] = Util.GetTypeIdentifier(fieldType);
                    }

                    var descAttr =
                        field.CustomAttributes.FirstOrDefault(attr =>
                            attr.AttributeType == RWTypes.DescriptionAttribute);
                    if (descAttr != null)
                        typeInfo.childDescriptions[fieldName] = (string) descAttr.ConstructorArguments[0].Value;
                }

                typeInfo.childCollected = true;
            }
        }

        private static IEnumerable<Type> SearchDerivedClasses(List<Assembly> assemblies)
        {
            var objType = typeof(object);
            Func<Type, bool> isRelated = type => // upstream to find base class in typeDict, if exists it is related.
            {
                if (typeDict.ContainsKey(type)) return false;
                var baseType = type.BaseType;
                while (baseType != null && baseType != objType)
                {
                    if (typeDict.ContainsKey(baseType) && assemblies.Contains(baseType.Assembly))
                    {
                        Log.Info(
                            $"{Util.GetTypeIdentifier(type)} has parent {Util.GetTypeIdentifier(baseType)} which is in typeDict");
                        return true;
                    }

                    baseType = baseType.BaseType;
                }

                return false;
            };

            try
            {
                var relatedTypes = from assem in AppDomain.CurrentDomain.GetAssemblies()
                    let types = assem.GetTypes()
                    from type in types
                    where type != null && isRelated(type)
                    // where type != null && abstractTypes.Any(type.IsSubclassOf)
                    select type;


                return relatedTypes;
            }
            catch (ReflectionTypeLoadException e)
            {
                Log.Error(
                    $"Errors getting {e.Types.Aggregate("", (str, type) => $"{str}, {type}")}:\n{e.LoaderExceptions.Aggregate("", (str, ex) => $"{str}\n{ex}")}");
                return new List<Type>();
            }
        }

        private static void PopulateData()
        {
            var integers = new HashSet<Type>(new[]
            {
                typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
                typeof(long), typeof(ulong)
            });

            var floats = new HashSet<Type>(new[]
            {
                typeof(float), typeof(double)
            });
            var stringType = typeof(string);

            var targets = typeDict;

            var def = RWTypes.Def;
            foreach (var pair in targets)
            {
                var type = pair.Key;
                var typeInfo = pair.Value;
                typeInfo.isLeafNode = true;
                if (type.IsEnum)
                {
                    var values = type.GetEnumValues().Cast<object>().Select(obj => obj.ToString())
                        .Select(name => new CompletionItem {label = name, kind = CompletionItemKind.Enum}).ToArray();
                    typeInfo.leafNodeCompletions = values;
                    typeInfo.specialType.@enum = true;
                }

                if (type.IsGenericType)
                {
                    if (type.GetGenericTypeDefinition() == typeof(List<>).GetGenericTypeDefinition())
                    {
                        var T = type.GetGenericArguments()[0];
                        ref var enumerable = ref typeInfo.specialType.enumerable;
                        enumerable.genericType = Util.GetTypeIdentifier(T);
                        enumerable.enumerableType = "list";
                    }

                    if (RWTypes.ISlateRef.IsAssignableFrom(type))
                        typeInfo.specialType.customFormats = new[] {"$slateRef"};
                }
                else if (type.IsArray)
                {
                    var T = type.GetElementType();
                    ref var enumerable = ref typeInfo.specialType.enumerable;
                    enumerable.genericType = Util.GetTypeIdentifier(T);
                    enumerable.enumerableType = "array";
                }

                if (type == stringType) typeInfo.specialType.@string = true;
                if (type.IsPrimitive)
                {
                    if (integers.Contains(type))
                        typeInfo.specialType.integer = true;
                    else if (floats.Contains(type)) typeInfo.specialType.@float = true;
                }

                if (type == typeof(bool)) typeInfo.specialType.@bool = true;
                if (type.IsSubclassOf(UnityEngineTypes.Color) || type == UnityEngineTypes.Color)
                {
                    typeInfo.specialType.color = true;
                    typeInfo.specialType.customFormats = new[] {"($r, $g, $b)", "($r, $g, $b, %a)"};
                }

                if (type == RWTypes.IntRange)
                {
                    typeInfo.specialType.intRange = true;
                    typeInfo.specialType.customFormats = new[] {"$min~$max"};
                }

                if (type == RWTypes.FloatRange)
                {
                    typeInfo.specialType.floatRange = true;
                    typeInfo.specialType.customFormats = new[] {"$min~$max"};
                }

                if (type == RWTypes.assembly.GetType("Verse.IntVec3"))
                {
                    typeInfo.specialType.intVec = true;
                    typeInfo.specialType.customFormats = new[] {"($x, $y, $z)"};
                }

                if (type == RWTypes.assembly.GetType("Verse.IntVec2"))
                {
                    typeInfo.specialType.intVec = true;
                    typeInfo.specialType.customFormats = new[] {"($x, $z)"};
                }

                if (type == UnityEngineTypes.assembly.GetType("UnityEngine.Vector2"))
                {
                    typeInfo.specialType.vector = true;
                    typeInfo.specialType.customFormats = new[] {"($x, $y)"};
                }

                if (type == UnityEngineTypes.assembly.GetType("UnityEngine.Vector3"))
                {
                    typeInfo.specialType.vector = true;
                    typeInfo.specialType.customFormats = new[] {"($x, $y, $z)"};
                }

                if (type.IsSubclassOf(def) || type == def)
                {
                    ref var defName = ref typeInfo.specialType.defName;
                    if (type.IsArray)
                    {
                        if (type.Assembly == RWTypes.assembly)
                            defName = type.GetElementType().Name;
                        else
                            defName = Util.GetArrayTypeIdentifier(type);
                    }
                    else
                    {
                        if (type.Assembly == RWTypes.assembly)
                            defName = type.Name;
                        else
                            defName = Util.GetTypeIdentifier(type);
                    }
                }

                if (type.Name.Contains("CompProperties")) typeInfo.specialType.comp = true;

                if (type.GetMethod("LoadDataFromXmlCustom",
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
                {
                    Log.Info($"Found custom load: {typeInfo.typeIdentifier}");
                    typeInfo.specialType.hasCustomReader = true;
                    if (type == RWTypes.DefHyperlink)
                    {
                        typeInfo.specialType.customXml.key = Util.GetSubclassTypeIdentifier(RWTypes.Def);
                        typeInfo.specialType.customXml.value = Util.GetTypeIdentifier(RWTypes.Def);
                        typeInfo.specialType.hyperlink = true;
                    }

                    if (type.Name.Contains("StatModifier"))
                    {
                        typeInfo.specialType.customXml.key = "RimWorld.StatDef";
                        typeInfo.specialType.customXml.value = "System.Single";
                    }

                    if (type.Name.Contains("ThingDefCountClass"))
                    {
                        typeInfo.specialType.customXml.key = "RimWorld.ThingDef";
                        typeInfo.specialType.customXml.value = "System.Int32";
                    }
                }

                if (type.IsAbstract) typeInfo.specialType.isAbstract = true;

                if (typeInfo.specialType.parent == null && type.BaseType != null && type.BaseType != typeof(object))
                {
                    Log.Warn(
                        $"Found parent info not registered: {typeInfo.typeIdentifier} has parent {Util.GetTypeIdentifier(type.BaseType)}");
                    typeInfo.specialType.parent = Util.GetTypeIdentifier(type.BaseType);
                }

                typeInfo.populated = true;
            }
        }

        private static class RWTypes
        {
            public static Assembly assembly;
            public static Type Def;
            public static Type UnsavedAttribute;
            public static Type IntRange, FloatRange;
            public static Type CompProperties;
            public static Type DescriptionAttribute;
            public static Type DefHyperlink;
            public static Type Entity;
            public static Type ISlateRef;
        }

        private static class UnityEngineTypes
        {
            public static Type Color;
            public static Assembly assembly;
        }
    }
}