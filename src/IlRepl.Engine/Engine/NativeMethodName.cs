using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Derives the exact diagnostic signatures of a method and its CoreCLR reference-sharing form.
/// </summary>
internal static class NativeMethodName
{
    /// <summary>
    /// Builds both concrete and canonical listing identities from original metadata signatures.
    /// </summary>
    /// <param name="method">The closed selected runtime method.</param>
    /// <returns>The permitted complete listing signatures.</returns>
    internal static string[] Listings(MethodBase method)
    {
        var owner = method.DeclaringType!;
        var definitionOwner = owner.IsConstructedGenericType ? owner.GetGenericTypeDefinition() : owner;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var definition = definitionOwner.GetMethods(flags).Cast<MethodBase>().Concat(definitionOwner.GetConstructors(flags))
            .Append(definitionOwner.TypeInitializer).FirstOrDefault(candidate => candidate?.MetadataToken == method.MetadataToken) ??
                method;
        if (definition.IsGenericMethod) definition = ((MethodInfo)definition).GetGenericMethodDefinition();
        var substitutions = new Dictionary<Type, Type>();
        foreach (var (parameter, argument) in definitionOwner.GetGenericArguments().Zip(owner.GetGenericArguments()))
            substitutions[parameter] = argument;
        if (method.IsGenericMethod)
            foreach (var (parameter, argument) in definition.GetGenericArguments().Zip(method.GetGenericArguments()))
                substitutions[parameter] = argument;
        return [.. new[] { Build(false), Build(true) }.Distinct(StringComparer.Ordinal)];

        string Build(bool canonical)
        {
            var name = TypeName(definitionOwner, alias: false) + ":" + definition.Name;
            if (definition.IsGenericMethod) name += "[" + string.Join(',', definition.GetGenericArguments().Select(t => TypeName(t))) + "]";
            name += "(" + string.Join(',', definition.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")";
            if (definition is MethodInfo info && info.ReturnType != typeof(void)) name += ":" + TypeName(info.ReturnType);
            if (!definition.IsStatic) name += ":this";
            return name;

            string TypeName(Type type, bool alias = true)
            {
                if (type.IsGenericParameter && substitutions.TryGetValue(type, out var argument))
                    return canonical && !argument.IsValueType ? "System.__Canon" : TypeName(argument, alias);
                if (type.IsByRef) return "byref";
                if (type.IsPointer || type.IsFunctionPointer) return "ptr";
                if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
                if (alias && type.IsEnum) return TypeName(Enum.GetUnderlyingType(type));
                if (alias && Aliases.TryGetValue(type, out var primitive)) return primitive;
                var baseType = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
                var result = baseType.FullName ?? baseType.Name;
                if (type.IsGenericType) result += "[" + string.Join(',', type.GetGenericArguments().Select(t => TypeName(t))) + "]";
                return result;
            }
        }
    }

    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(char)] = "char", [typeof(sbyte)] = "sbyte",
        [typeof(byte)] = "byte", [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int",
        [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(nint)] = "nint",
        [typeof(nuint)] = "nuint", [typeof(float)] = "float", [typeof(double)] = "double",
    };
}
