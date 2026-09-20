using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Retains complete copied type contexts when runtime reflection can reach members without static IL references.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private string? _reflectionLocation;
    private readonly HashSet<MethodBase> _metadataOnlyMethods = [];
    private readonly HashSet<Type> _runtimeHelperTypes = [];

    private void AddRuntimeHelper(MethodInfo method)
    {
        _runtimeHelperTypes.Add(method.DeclaringType!);
        AddMethod(method);
    }

    private void ScanReflection()
    {
        if (_reflectionLocation is null)
        {
            return;
        }

        foreach (var type in _types.Keys.ToArray())
        {
            if (_runtimeHelperTypes.Contains(type))
            {
                continue;
            }

            foreach (var nested in type.GetNestedTypes(Declared))
            {
                AddType(nested);
            }

            foreach (var member in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                AddMethod(member, metadataOnly: true);
                _dependencies.Add(new EditDependency(MemberResolver.Describe(member), member.Module.Assembly.FullName!,
                    _reflectionLocation + ": reflective access", "copied") { Access = MemberAccess.AccessWord(member.Attributes) });
            }
        }
    }

    private static bool HasNonIlImplementation(MethodBase method)
    {
        var flags = method.GetMethodImplementationFlags();
        return (flags & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL
            || flags.HasFlag(MethodImplAttributes.InternalCall);
    }

    private static bool ReflectsMembers(MethodBase method)
    {
        var type = method.DeclaringType;
        var createsDelegateByName = type == typeof(Delegate) && method.Name == nameof(Delegate.CreateDelegate)
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string));
        var reflectsOnType = type is not null && (typeof(Type).IsAssignableFrom(type) || type == typeof(IReflect));
        var looksUp = method.Name.StartsWith("Get", StringComparison.Ordinal) || method.Name.StartsWith("Find", StringComparison.Ordinal)
            || method.Name.StartsWith("get_Declared", StringComparison.Ordinal) || method.Name == nameof(Type.InvokeMember);
        return type == typeof(Activator) || type == typeof(object) && method.Name == nameof(GetType)
            || createsDelegateByName || reflectsOnType && looksUp
            || type == typeof(RuntimeReflectionExtensions);
    }

    private static bool EnumeratesAssemblyTypes(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type?.Assembly != typeof(Assembly).Assembly)
        {
            return false;
        }

        return typeof(Assembly).IsAssignableFrom(type)
                && method.Name is nameof(Assembly.GetTypes) or nameof(Assembly.GetExportedTypes)
                    or nameof(Assembly.GetForwardedTypes) or "get_DefinedTypes" or "get_ExportedTypes"
            || typeof(Module).IsAssignableFrom(type) && method.Name is nameof(Module.GetTypes) or nameof(Module.FindTypes);
    }

    private static bool InspectsAssemblyResources(MethodBase method) => method.DeclaringType is { } type
        && type.Assembly == typeof(Assembly).Assembly && typeof(Assembly).IsAssignableFrom(type)
        && method.Name is nameof(Assembly.GetManifestResourceStream) or nameof(Assembly.GetManifestResourceNames)
            or nameof(Assembly.GetManifestResourceInfo);

    private static bool InspectsAssemblyAttributes(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type?.Assembly != typeof(Assembly).Assembly
            || method.Name is not (nameof(Assembly.GetCustomAttributes) or nameof(Assembly.GetCustomAttributesData)
                or nameof(Assembly.IsDefined) or nameof(Attribute.GetCustomAttribute) or "get_CustomAttributes"))
        {
            return false;
        }

        if (typeof(Assembly).IsAssignableFrom(type) || typeof(Module).IsAssignableFrom(type)
            || type == typeof(ICustomAttributeProvider))
        {
            return true;
        }

        if (type != typeof(Attribute) && type != typeof(CustomAttributeExtensions) && type != typeof(CustomAttributeData))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length != 0
            && (parameters[0].ParameterType == typeof(Assembly) || parameters[0].ParameterType == typeof(Module));
    }

    private static string? AssemblyInspectionProblem(MethodBase method)
    {
        if (MetadataReferenceProblem(method) is { } referenceProblem)
        {
            return referenceProblem;
        }

        static bool ResolvesToken(Type type, string name) => typeof(Module).IsAssignableFrom(type)
            ? name is nameof(Module.ResolveMethod) or nameof(Module.ResolveField) or nameof(Module.ResolveType)
                or nameof(Module.ResolveMember) or nameof(Module.ResolveString) or nameof(Module.ResolveSignature)
            : type == typeof(ModuleHandle) && name is nameof(ModuleHandle.ResolveMethodHandle)
                or nameof(ModuleHandle.ResolveFieldHandle) or nameof(ModuleHandle.ResolveTypeHandle)
                or nameof(ModuleHandle.GetRuntimeMethodHandleFromMetadataToken)
                or nameof(ModuleHandle.GetRuntimeFieldHandleFromMetadataToken)
                or nameof(ModuleHandle.GetRuntimeTypeHandleFromMetadataToken);
        if (method.DeclaringType is { } tokenType && tokenType.Assembly == typeof(Module).Assembly
            && ResolvesToken(tokenType, method.Name))
        {
            return "module token resolution cannot reproduce the original metadata tokens";
        }

        if (method.DeclaringType is { } type && type.Assembly == typeof(Assembly).Assembly
            && typeof(Assembly).IsAssignableFrom(type))
        {
            if (method.Name == nameof(Assembly.GetSatelliteAssembly))
            {
                return "satellite assembly lookup cannot reproduce the original satellite context";
            }

            if (method.Name is nameof(Assembly.GetModule) or nameof(Assembly.GetModules) or nameof(Assembly.GetLoadedModules)
                or "get_Modules")
            {
                return "assembly module inspection cannot reproduce the original module table";
            }

            if (method.Name is "get_Location" or "get_CodeBase" or "get_EscapedCodeBase" or nameof(Assembly.GetFile)
                or nameof(Assembly.GetFiles))
            {
                return "assembly file inspection cannot reproduce the original assembly file context";
            }

            if (method.Name is nameof(Assembly.GetName) or "get_FullName" or nameof(ToString) or "get_ImageRuntimeVersion"
                or "get_IsDynamic" or "get_IsCollectible" or "get_EntryPoint")
            {
                return "assembly identity inspection cannot reproduce the original assembly identity";
            }

            if (method.Name == nameof(Assembly.GetReferencedAssemblies))
            {
                return "referenced assembly inspection cannot reproduce the original reference table";
            }
        }

        if (method.DeclaringType == typeof(ModuleHandle) && method.Name == "get_MDStreamVersion")
        {
            return "module identity inspection cannot reproduce the original module metadata";
        }

        if (method.DeclaringType is { } globalType && globalType.Assembly == typeof(Module).Assembly
            && typeof(Module).IsAssignableFrom(globalType)
            && method.Name is nameof(Module.GetMethods) or nameof(Module.GetMethod) or nameof(Module.GetFields) or nameof(Module.GetField))
        {
            return "module global inspection cannot reproduce the original global members";
        }

        if (method.DeclaringType is { } moduleType && moduleType.Assembly == typeof(Module).Assembly
            && typeof(Module).IsAssignableFrom(moduleType)
            && method.Name is "get_Name" or "get_ScopeName" or "get_FullyQualifiedName" or "get_ModuleVersionId"
                or "get_MDStreamVersion" or nameof(Module.GetPEKind) or nameof(ToString))
        {
            return "module identity inspection cannot reproduce the original module metadata";
        }

        if (InspectsAssemblyAttributes(method))
        {
            return "assembly and module attribute inspection cannot reproduce the original metadata";
        }

        if (InspectsAssemblyResources(method))
        {
            return "manifest resource inspection cannot reproduce the original assembly's resources";
        }

        return EnumeratesAssemblyTypes(method)
            ? "assembly and module type enumeration cannot reproduce the original assembly's complete type set" : null;
    }

    private string? AssemblyInspectionProblem(MethodBase method, object?[]? receivers)
    {
        if (IsMemberTokenInspection(method))
        {
            return MemberTokenProblem(receivers);
        }

        return TypeNameProblem(method, receivers) ?? AssemblyInspectionProblem(method);
    }
}
