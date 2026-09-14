using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Discovers custom marshaler types and their runtime factories before metadata emission.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private void ScanMarshalling()
    {
        foreach (var type in _types.Keys.ToArray())
        {
            foreach (var field in type.GetFields(Declared).Where(field => field.Attributes.HasFlag(FieldAttributes.HasFieldMarshal)))
            {
                ScanMarshalling(field.Module, field.MetadataToken, type, field + ": field marshalling");
            }
        }

        foreach (var method in _methods.Keys.ToArray())
        {
            var parameters = method is MethodInfo info ? method.GetParameters().Prepend(info.ReturnParameter) : method.GetParameters();
            foreach (var parameter in parameters.Where(parameter => ImportedMarshalling.Attributes(parameter)
                .HasFlag(ParameterAttributes.HasFieldMarshal)))
            {
                var location = MemberResolver.Describe(method)
                    + (parameter.Position < 0 ? ": return" : ": parameter " + parameter.Position);
                ScanMarshalling(method.Module, ImportedMarshalling.ParameterToken(parameter), method.DeclaringType!,
                    location + " marshalling");
            }
        }
    }

    private void ScanMarshalling(Module module, int token, Type owner, string location)
    {
        try
        {
            if (ImportedMarshalling.CustomMarshaler(module, token, _session.Resolver) is not { } type)
            {
                return;
            }

            ScanSignatureType(type, owner, location);
            if (_types.ContainsKey(DefinitionOf(type)) && type.GetMethod("GetInstance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)]) is { } factory)
            {
                AddMethod(factory);
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or TypeLoadException or FileNotFoundException)
        {
            throw new ReplException($"invalid marshalling metadata on {location}: {exception.Message}", exception);
        }
    }
}
