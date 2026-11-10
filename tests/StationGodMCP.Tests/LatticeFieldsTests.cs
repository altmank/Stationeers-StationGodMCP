#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// In a game without the optional Lattice mod, StationGod 2.16.1 logged about 68 TypeLoadException lines naming its own
/// LatticeLink types: StationeersLaunchPad's entrypoint search reads every type of a mod's assemblies (three passes,
/// Type.IsAssignableFrom), and HarmonyX's AccessTools.GetTypesFromAssembly calls Assembly.GetTypes(). Neither is
/// StationGod's to skip. Mono lays a class out, every field's type resolved, the first time anything reads it, so a
/// field whose type names Lattice.Api (directly, as a generic argument, or in a compiler-made closure or lambda cache)
/// fails then, and so does every type with a field of that failed type. Method bodies are compiled only when run,
/// so Lattice may be named freely there: only the layout must not name it. These tests read the compiled metadata,
/// with Lattice.Api nowhere to load from, so they see what Mono sees.
/// </summary>
public sealed class LatticeFieldsTests
{
    private const string Lattice = "Lattice.Api";
    private const System.Reflection.TypeAttributes StaticClass =
        System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Sealed;

    /// <summary>
    /// The test assembly compiles the mod's LatticeLink/Json and LatticeLink/Model sources (its own test classes, which
    /// run with Lattice's test host, are not the mod's and are left out).
    /// </summary>
    [Fact]
    public void NoTypeInTheLatticeLinkSourcesHereLaysOutALatticeType()
    {
        AssertNone(LayoutNaming(typeof(LatticeFieldsTests).Assembly.Location, Lattice, "StationGodMCP.LatticeLink"));
    }

    /// <summary>
    /// The built mod assembly, every LatticeLink file in it: build.ps1 runs this once the mod is built, with
    /// SG_MOD_DLL set to it. Without SG_MOD_DLL (the plain test run, before the mod is built) there is nothing to read.
    /// </summary>
    [Fact]
    public void NoTypeInTheBuiltModLaysOutALatticeType()
    {
        string? modDll = Environment.GetEnvironmentVariable("SG_MOD_DLL");
        if (string.IsNullOrEmpty(modDll))
        {
            return;
        }

        Assert.True(File.Exists(modDll), $"SG_MOD_DLL names no file: {modDll}");
        AssertNone(LayoutNaming(modDll!, Lattice));
    }

    private static void AssertNone(List<string> found) =>
        Assert.True(found.Count == 0, "Fields that name Lattice.Api:\n" + string.Join("\n", found));

    [Fact]
    public void TheCheckFindsAFieldThatNamesTheAssembly()
    {
        // This assembly's own Witness fields name xunit's assembly the way a LatticeLink field named Lattice.Api.
        List<string> found = LayoutNaming(typeof(LatticeFieldsTests).Assembly.Location, "xunit.core");

        Assert.Contains(found, f => f.Contains("Witness:Direct", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("Witness:InGeneric", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("Witness:InArray", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each field, base type or interface in an assembly's types (those under underNamespace, when given) that names a
    /// type from assemblyName.
    /// </summary>
    internal static List<string> LayoutNaming(string path, string assemblyName, string? underNamespace = null)
    {
        List<string> found = new List<string>();
        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        Names provider = new Names(reader, assemblyName);
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if ((type.Attributes & StaticClass) == StaticClass)
            {
                // A static class: nothing lays it out before it is used (the scans skip abstract types, and 2.16.1's
                // log names no static LatticeLink class), and LatticePresence uses none of them without Lattice.
                continue;
            }

            string typeName = provider.GetTypeFromDefinition(reader, handle, 0).Name;
            if (underNamespace != null && !typeName.StartsWith(underNamespace + ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!type.BaseType.IsNil && provider.Of(type.BaseType).Names)
            {
                found.Add($"{typeName} : {provider.Of(type.BaseType).Name}");
            }

            foreach (InterfaceImplementationHandle iface in type.GetInterfaceImplementations())
            {
                Sig sig = provider.Of(reader.GetInterfaceImplementation(iface).Interface);
                if (sig.Names)
                {
                    found.Add($"{typeName} : {sig.Name}");
                }
            }

            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                Sig sig = field.DecodeSignature(provider, null);
                if (sig.Names)
                {
                    found.Add($"{typeName}:{reader.GetString(field.Name)} ({sig.Name})");
                }
            }
        }

        return found;
    }

    internal readonly record struct Sig(string Name, bool Names);

    /// <summary>Decodes a signature into its name and whether any part of it comes from the watched assembly.</summary>
    private sealed class Names : ISignatureTypeProvider<Sig, object?>
    {
        private readonly MetadataReader _reader;
        private readonly string _assembly;

        internal Names(MetadataReader reader, string assembly)
        {
            _reader = reader;
            _assembly = assembly;
        }

        internal Sig Of(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(_reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => GetTypeFromReference(_reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(_reader, null, (TypeSpecificationHandle)handle, 0),
            _ => new Sig(handle.Kind.ToString(), false),
        };

        public Sig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string name = reader.GetString(type.Name);
            TypeDefinitionHandle outer = type.GetDeclaringType();
            return outer.IsNil
                ? new Sig(Join(reader.GetString(type.Namespace), name), false)
                : new Sig(GetTypeFromDefinition(reader, outer, 0).Name + "+" + name, false);
        }

        public Sig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            string name = reader.GetString(type.Name);
            EntityHandle scope = type.ResolutionScope;
            switch (scope.Kind)
            {
                case HandleKind.TypeReference:
                    Sig outer = GetTypeFromReference(reader, (TypeReferenceHandle)scope, 0);
                    return new Sig(outer.Name + "+" + name, outer.Names);
                case HandleKind.AssemblyReference:
                    AssemblyReference assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                    return new Sig(Join(reader.GetString(type.Namespace), name),
                        reader.GetString(assembly.Name) == _assembly);
                default:
                    return new Sig(Join(reader.GetString(type.Namespace), name), false);
            }
        }

        public Sig GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);

        public Sig GetGenericInstantiation(Sig genericType, ImmutableArray<Sig> typeArguments)
        {
            bool names = genericType.Names;
            string[] args = new string[typeArguments.Length];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = typeArguments[i].Name;
                names |= typeArguments[i].Names;
            }

            return new Sig($"{genericType.Name}<{string.Join(",", args)}>", names);
        }

        public Sig GetArrayType(Sig elementType, ArrayShape shape) => elementType with { Name = elementType.Name + "[,]" };
        public Sig GetSZArrayType(Sig elementType) => elementType with { Name = elementType.Name + "[]" };
        public Sig GetByReferenceType(Sig elementType) => elementType with { Name = elementType.Name + "&" };
        public Sig GetPointerType(Sig elementType) => elementType with { Name = elementType.Name + "*" };
        public Sig GetPinnedType(Sig elementType) => elementType;
        public Sig GetModifiedType(Sig modifier, Sig unmodifiedType, bool isRequired) => unmodifiedType;
        public Sig GetPrimitiveType(PrimitiveTypeCode typeCode) => new Sig(typeCode.ToString(), false);
        public Sig GetGenericTypeParameter(object? context, int index) => new Sig("!" + index, false);
        public Sig GetGenericMethodParameter(object? context, int index) => new Sig("!!" + index, false);
        public Sig GetFunctionPointerType(MethodSignature<Sig> signature) => new Sig("fnptr", false);

        private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    }

    // Fields naming xunit.core's types, for TheCheckFindsAFieldThatNamesTheAssembly.
#pragma warning disable CS0169, CS0649
    private sealed class Witness
    {
        private FactAttribute? Direct;
        private List<FactAttribute>? InGeneric;
        private FactAttribute[]? InArray;
    }
#pragma warning restore CS0169, CS0649
}
