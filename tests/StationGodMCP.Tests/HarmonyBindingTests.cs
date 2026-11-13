using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Xunit;

namespace StationGodMCP.Tests
{
    /// <summary>
    /// Harmony injects a patch method's parameter by name: a name other than the Harmony specials (<c>__instance</c>, <c>__result</c>, <c>__state</c>,
    /// <c>___field</c>...) must be a parameter of the target method, or HarmonyX refuses the patch at install ("Parameter ... not found") and the
    /// feature silently never starts. This reads every StationGod patch method's source, works out the game method it targets (the
    /// [HarmonyPatch] attribute on its class or method) and checks each named injected parameter against the real game assembly's metadata.
    /// Skipped without the game's Assembly-CSharp.dll (STATIONGOD_GAME_ASSEMBLY, else STATIONEERS_DIR, else the default Steam install).
    /// </summary>
    public sealed class HarmonyBindingTests
    {
        /// <summary>Patches installed with harmony.Patch(...) by hand: "Class.Method" -> game type and method. StationGod installs none by name.</summary>
        private static readonly Dictionary<string, (string Type, string Method)> HandInstalled = new();

        private static readonly Regex PatchMethod = new(
            @"(?<attrs>(?:[ \t]*\[[^\r\n]*\][ \t]*\r?\n)*)\s*(?:private|internal|public)\s+static\s+[\w<>\[\],\s\.\?]+?\s+(?<name>\w+)\s*\((?<params>[^)]*)\)\s*(?:=>|\{)",
            RegexOptions.Compiled);

        private static readonly Regex ClassDecl = new(@"(?<attrs>(?:[ \t]*\[[^\r\n]*\][ \t]*\r?\n)*)[ \t]*(?:internal|public|private)?\s*(?:static\s+|sealed\s+|abstract\s+)*class\s+(?<name>\w+)", RegexOptions.Compiled);

        [Fact]
        public void Every_named_injected_parameter_of_a_StationGod_patch_exists_on_its_game_target()
        {
            string gamePath = GameAssemblyPath();
            if (gamePath == null)
            {
                return;   // skipped: no game assembly on this machine
            }
            Dictionary<string, List<(TypeDefinition Type, MetadataReader Md)>> types = ReadTypes(gamePath);
            var problems = new List<string>();
            int checkedPatches = 0;
            var resolved = new List<string>();
            string srcMod = Path.Combine(RepoRoot(), "src", "StationGodMCP.Mod");
            foreach (string file in Directory.GetFiles(srcMod, "*.cs", SearchOption.AllDirectories))
            {
                string text = JoinContinuedLines(File.ReadAllText(file));
                foreach (ClassBlock block in Classes(text))
                {
                    string classTargetType = FirstTypeof(block.Attrs);
                    string pickedType = Regex.Match(block.Body, @"typeof\((\w+)\)\.GetMethods").Groups[1].Value;
                    string pickedName = Regex.Match(block.Body, @"m\.Name == nameof\(\w+\.(\w+)\)").Groups[1].Value;
                    foreach (Match m in PatchMethod.Matches(block.Body))
                    {
                        string methodName = m.Groups["name"].Value;
                        string[] named = NamedParameters(m.Groups["params"].Value);
                        bool patchLike = IsPatchLike(m.Groups["attrs"].Value, methodName) || HandInstalled.ContainsKey(block.Name + "." + methodName);
                        if (named.Length == 0 || !patchLike || methodName == "TargetMethod" || methodName == "Transpiler")
                        {
                            continue;
                        }
                        string label = block.Name + "." + methodName + " (" + Path.GetFileName(file) + ")";
                        (string type, string method) = Target(block, m, classTargetType, pickedType, pickedName);
                        if (type == null)
                        {
                            problems.Add(label + ": cannot tell which game method it patches; name it in an attribute or HandInstalled");
                            continue;
                        }
                        checkedPatches++;
                        resolved.Add(label + " -> " + type + "." + method);
                        if (!types.TryGetValue(type, out var defs))
                        {
                            problems.Add(label + ": game type " + type + " not found");
                            continue;
                        }
                        List<HashSet<string>> overloads = defs.SelectMany(d => Overloads(d.Md, d.Type, method)).ToList();
                        if (overloads.Count == 0)
                        {
                            problems.Add(label + ": " + type + "." + method + " not found");
                        }
                        else if (!overloads.Any(o => named.All(o.Contains)))
                        {
                            problems.Add(label + ": parameter(s) " + string.Join(", ", named.Where(n => !overloads.Any(o => o.Contains(n)))) + " not on " + type + "." + method
                                + " (it has " + string.Join(" | ", overloads.Select(o => string.Join(",", o))) + ")");
                        }
                    }
                }
            }
            Assert.True(checkedPatches >= 15, "only " + checkedPatches + " patches were resolved; the scan is not reading the sources:" + Environment.NewLine + string.Join(Environment.NewLine, resolved));
            Assert.True(problems.Count == 0, "Harmony would refuse these patches:\n" + string.Join("\n", problems));
        }

        /// <summary>An attribute or parameter list written over several lines reads as one line, so the scan sees a whole [HarmonyPatch(...)].</summary>
        private static string JoinContinuedLines(string text) => Regex.Replace(text, @",[ \t]*\r?\n[ \t]+", ", ");

        private static (string Type, string Method) Target(ClassBlock block, Match m, string classType, string pickedType, string pickedName)
        {
            if (HandInstalled.TryGetValue(block.Name + "." + m.Groups["name"].Value, out var hand))
            {
                return hand;
            }
            string attrs = m.Groups["attrs"].Value;
            string type = FirstTypeof(attrs) ?? classType;
            string method = AttributeMethod(attrs) ?? AttributeMethod(block.Attrs);
            if (type != null && method != null && Regex.IsMatch(attrs + block.Attrs, @"HarmonyPatch"))
            {
                return (type, method);
            }
            if (pickedType.Length > 0 && pickedName.Length > 0)
            {
                return (pickedType, pickedName);
            }
            return (null, null);
        }

        private static bool IsPatchLike(string attrs, string name) =>
            attrs.Contains("Harmony") || Regex.IsMatch(name, "^(Prefix|Postfix|Finalizer|Transpiler)$|(Prefix|Postfix)$");

        private static string FirstTypeof(string attrs) => Regex.Match(attrs, @"HarmonyPatch\(\s*typeof\((\w+)\)") is { Success: true } t ? t.Groups[1].Value : null;

        private static string AttributeMethod(string attrs)
        {
            Match nm = Regex.Match(attrs, @"HarmonyPatch\(.*?nameof\(\w+\.(\w+)\)");
            if (nm.Success)
            {
                return nm.Groups[1].Value;
            }
            Match str = Regex.Match(attrs, "HarmonyPatch\\((?:\\s*typeof\\(\\w+\\)\\s*,)?\\s*\"(\\w+)\"");
            return str.Success ? str.Groups[1].Value : null;
        }

        /// <summary>The parameter names Harmony must find on the target: everything except its double- and triple-underscore specials.</summary>
        private static string[] NamedParameters(string parameterList) =>
            parameterList.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last())
                .Where(n => !n.StartsWith("__")).ToArray();

        private readonly record struct ClassBlock(string Name, string Attrs, string Body);

        private static IEnumerable<ClassBlock> Classes(string text)
        {
            MatchCollection all = ClassDecl.Matches(text);
            for (int i = 0; i < all.Count; i++)
            {
                int start = all[i].Index + all[i].Length;
                int end = i + 1 < all.Count ? all[i + 1].Index : text.Length;
                yield return new ClassBlock(all[i].Groups["name"].Value, all[i].Groups["attrs"].Value, text.Substring(start, end - start));
            }
        }

        private static string GameAssemblyPath()
        {
            string path = Environment.GetEnvironmentVariable("STATIONGOD_GAME_ASSEMBLY");
            if (string.IsNullOrEmpty(path))
            {
                string install = Environment.GetEnvironmentVariable("STATIONEERS_DIR");
                if (string.IsNullOrEmpty(install))
                {
                    install = Path.Combine("C:", "Program Files (x86)", "Steam", "steamapps", "common", "Stationeers");
                }

                path = Path.Combine(install, "rocketstation_Data", "Managed", "Assembly-CSharp.dll");
            }

            return File.Exists(path) ? path : null;
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "StationGodMCP.csproj")))
            {
                dir = Path.GetDirectoryName(dir);
            }
            return dir ?? throw new InvalidOperationException("repo root not found");
        }

        private static Dictionary<string, List<(TypeDefinition, MetadataReader)>> ReadTypes(string path)
        {
            var peReader = new PEReader(File.OpenRead(path));
            MetadataReader md = peReader.GetMetadataReader();
            var map = new Dictionary<string, List<(TypeDefinition, MetadataReader)>>();
            foreach (TypeDefinitionHandle h in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(h);
                string name = md.GetString(td.Name);
                if (!map.TryGetValue(name, out var list))
                {
                    map[name] = list = new List<(TypeDefinition, MetadataReader)>();
                }
                list.Add((td, md));
            }
            return map;
        }

        /// <summary>Parameter-name sets of every method called <paramref name="method"/> on the type and the game base types it derives from.</summary>
        private static IEnumerable<HashSet<string>> Overloads(MetadataReader md, TypeDefinition type, string method)
        {
            TypeDefinition? current = type;
            while (current != null)
            {
                foreach (MethodDefinitionHandle mh in current.Value.GetMethods())
                {
                    MethodDefinition def = md.GetMethodDefinition(mh);
                    if (md.GetString(def.Name) == method)
                    {
                        yield return new HashSet<string>(def.GetParameters().Select(p => md.GetString(md.GetParameter(p).Name)).Where(n => n.Length > 0));
                    }
                }
                EntityHandle baseHandle = current.Value.BaseType;
                current = baseHandle.Kind == HandleKind.TypeDefinition ? md.GetTypeDefinition((TypeDefinitionHandle)baseHandle) : null;
            }
        }
    }
}
