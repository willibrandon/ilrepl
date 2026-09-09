using System.Reflection;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

public sealed partial class DefinitionAssemblyTests
{
    /// <summary>
    /// Cecil-written images shaped like the ones the engine will write.
    /// </summary>
    private static class Images
    {
        /// <summary>
        /// Loads a collectible assembly containing the named point type.
        /// </summary>
        public static DefinitionAssembly LoadPoint(string typeName = "Point")
        {
            var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
            var assembly = New(name);
            var module = assembly.MainModule;
            var point = new TypeDefinition("", typeName, TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(
                object)));
            module.Types.Add(point);
            point.Methods.Add(Constructor(module));
            point.Methods.Add(Returning(module, "Value", MethodAttributes.Public | MethodAttributes.Static, 7));
            point.Methods.Add(Returning(module, "Secret", MethodAttributes.Assembly | MethodAttributes.Static, 9));
            return SessionAssemblies.Load(Write(assembly), name, SessionAssemblyKind.Types, []);
        }

        /// <summary>
        /// Loads a collectible holder whose field references the supplied point assembly.
        /// </summary>
        public static DefinitionAssembly LoadHolder(DefinitionAssembly point)
        {
            var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
            var assembly = New(name);
            var module = assembly.MainModule;
            IgnoreAccessChecksTo(module, point.Name);
            var reference = new AssemblyNameReference(point.Name, SessionAssemblies.Version);
            module.AssemblyReferences.Add(reference);
            var pointType = new TypeReference("", "Point", module, reference);
            var holder = new TypeDefinition("", "Holder", TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(
                object)));
            module.Types.Add(holder);
            var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            var il = read.Body.GetILProcessor();
            il.Emit(OpCodes.Call, new MethodReference("Value", module.TypeSystem.Int32, pointType) { HasThis = false });
            il.Emit(OpCodes.Call, new MethodReference("Secret", module.TypeSystem.Int32, pointType) { HasThis = false });
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
            holder.Methods.Add(read);
            var attribute = new CustomAttribute(module.ImportReference(typeof(System.Diagnostics.DebuggerTypeProxyAttribute).GetConstructor(
                [typeof(Type)])!));
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(Type)), pointType));
            holder.CustomAttributes.Add(attribute);
            return SessionAssemblies.Load(Write(assembly), name, SessionAssemblyKind.Types, [point]);
        }

        /// <summary>
        /// Loads a counting type with dependencies on the earlier definition assemblies.
        /// </summary>
        public static DefinitionAssembly LoadCounting(int index, IReadOnlyList<DefinitionAssembly> earlier)
        {
            var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
            var assembly = New(name);
            var module = assembly.MainModule;
            var type = new TypeDefinition("", "C" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(object)));
            module.Types.Add(type);
            var sum = new MethodDefinition("Sum", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            var il = sum.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_0);
            foreach (var dependency in earlier)
            {
                var reference = new AssemblyNameReference(dependency.Name, SessionAssemblies.Version);
                module.AssemblyReferences.Add(reference);
                var other = new TypeReference("", dependency.Assembly.GetTypes()[0].Name, module, reference);
                il.Emit(OpCodes.Call, new MethodReference("One", module.TypeSystem.Int32, other) { HasThis = false });
                il.Emit(OpCodes.Add);
            }

            il.Emit(OpCodes.Ret);
            type.Methods.Add(sum);
            type.Methods.Add(Returning(module, "One", MethodAttributes.Public | MethodAttributes.Static, 1));
            return SessionAssemblies.Load(Write(assembly), name, SessionAssemblyKind.Types, earlier);
        }

        /// <summary>
        /// Creates a standalone assembly image containing the named public type.
        /// </summary>
        public static byte[] Standalone(string typeName)
        {
            var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("IlReplForeign." + typeName, new Version(1, 0, 0,
                0)), "M", ModuleKind.Dll);
            var module = assembly.MainModule;
            var type = new TypeDefinition("", typeName, TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(
                object)));
            module.Types.Add(type);
            type.Methods.Add(Constructor(module));
            return Write(assembly);
        }

        private static AssemblyDefinition New(string name) =>
            AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, SessionAssemblies.Version), "M", ModuleKind.Dll);

        private static byte[] Write(AssemblyDefinition assembly)
        {
            using var stream = new MemoryStream();
            assembly.Write(stream);
            return stream.ToArray();
        }

        private static MethodDefinition Returning(ModuleDefinition module, string name, MethodAttributes attributes, int value)
        {
            var method = new MethodDefinition(name, attributes, module.TypeSystem.Int32);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, value);
            il.Emit(OpCodes.Ret);
            return method;
        }

        private static MethodDefinition Constructor(ModuleDefinition module)
        {
            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(OpCodes.Ret);
            return ctor;
        }

        private static void IgnoreAccessChecksTo(ModuleDefinition module, string target)
        {
            var attribute = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute", TypeAttributes.Public
                | TypeAttributes.Class, module.ImportReference(typeof(Attribute)));
            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            ctor.Parameters.Add(new ParameterDefinition("assemblyName", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.String));
            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                Type.EmptyTypes)!));
            il.Emit(OpCodes.Ret);
            attribute.Methods.Add(ctor);
            module.Types.Add(attribute);
            var applied = new CustomAttribute(ctor);
            applied.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, target));
            module.Assembly.CustomAttributes.Add(applied);
        }
    }
}
