using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Captures module initialization and runs its copied dependencies when an emitted module initializes.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private readonly Dictionary<Module, Type> _moduleOwners = [];
    private readonly Dictionary<Module, MethodBase> _moduleInitializers = [];
    private readonly HashSet<MethodBase> _initializationMethods = [];

    private bool IsModuleInitializer(MethodBase method) => _moduleInitializers.TryGetValue(method.Module, out var initializer)
        && initializer == method;

    private Type ContextOf(MethodBase method) => IsModuleInitializer(method) ? _moduleOwners[method.Module] : method.DeclaringType!;

    private void CaptureModule(Type owner)
    {
        if (!_moduleOwners.TryAdd(owner.Module, owner) || ModuleMetadata.TryOpen(owner.Module) is not { } metadata)
        {
            return;
        }

        var module = metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1));
        foreach (var handle in module.GetMethods())
        {
            var definition = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(definition.Name) != ".cctor")
            {
                continue;
            }

            var initializer = owner.Module.ResolveMethod(MetadataTokens.GetToken(handle))!;
            _moduleInitializers.Add(owner.Module, initializer);
            AddMethod(initializer, initialization: true);
        }
    }

    private Dictionary<Module, TypeDefinition> DefineModuleOwners(CecilWriter writer)
    {
        var owners = new Dictionary<Module, TypeDefinition>();
        foreach (var module in _moduleInitializers.Keys)
        {
            var owner = new TypeDefinition("IlRepl.Edits." + Name, "<Module" + owners.Count.ToString(CultureInfo.InvariantCulture) + ">",
                TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, writer.Object);
            writer.Module.Types.Add(owner);
            owners.Add(module, owner);
        }

        return owners;
    }

    private void WriteModuleInitializers(CecilWriter writer, Dictionary<MemberInfo, IMemberDefinition> definitions)
    {
        if (_moduleInitializers.Count == 0)
        {
            return;
        }

        var owner = writer.Module.Types[0];
        var initializer = owner.Methods.SingleOrDefault(method => method.Name == ".cctor");
        if (initializer is null)
        {
            initializer = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, writer.Module.TypeSystem.Void);
            owner.Methods.Add(initializer);
            initializer.Body.GetILProcessor().Emit(OpCodes.Ret);
        }

        var il = initializer.Body.GetILProcessor();
        var end = initializer.Body.Instructions.Last();
        foreach (var original in _moduleInitializers.Values)
        {
            var copy = (MethodDefinition)definitions[original];
            copy.Name = "Initialize";
            copy.Attributes = MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig;
            il.InsertBefore(end, il.Create(OpCodes.Call, copy));
        }
    }
}
