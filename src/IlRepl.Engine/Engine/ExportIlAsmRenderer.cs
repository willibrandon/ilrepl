using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Mono.Cecil;
using FieldDefinition = Mono.Cecil.FieldDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;
using ReflectionFieldAttributes = System.Reflection.FieldAttributes;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionTypeAttributes = System.Reflection.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Renders the exported assembly so copied declarations and executable references share the saved metadata.
/// </summary>
internal sealed partial class ExportIlAsmRenderer
{
    private readonly StringBuilder _text = new();
    private readonly MetadataSignatureProvider _signatures = new(_ => null);
    private readonly MetadataReader _metadata;
    private readonly PEReader _pe;
    private readonly string _assembly;
    private readonly Session? _draft;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private int _indent;

    private ExportIlAsmRenderer(PEReader pe, string assembly, Session? draft)
    {
        _pe = pe;
        _metadata = pe.GetMetadataReader();
        _assembly = assembly;
        _draft = draft;
    }

    /// <summary>
    /// Renders every exported declaration and the current cell from one frozen image.
    /// </summary>
    /// <param name="session">The session to export.</param>
    /// <returns>Standalone Microsoft ILAsm source.</returns>
    internal static string Render(Session session)
    {
        Session? draft = null;
        try
        {
            CellCompiler.RequireComplete(session);
        }
        catch (ReplException)
        {
            draft = session;
        }

        var bytes = draft is null ? AssemblyExporter.Write(session, "ilrepl_cell") : AssemblyExporter.WriteDeclarations(session);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        using var module = ModuleDefinition.ReadModule(new MemoryStream(bytes, writable: false));
        var renderer = new ExportIlAsmRenderer(pe, module.Assembly.Name.Name, draft);
        renderer.WriteModule(module);
        return renderer._text.ToString();
    }

    private void WriteModule(ModuleDefinition module)
    {
        foreach (var type in module.GetTypes().Cast<TypeReference>().Concat(module.GetTypeReferences()))
        {
            var scope = type.Scope is AssemblyNameReference reference ? reference.Name : _assembly;
            _names["[" + scope + "]" + type.FullName] = ScopedName(type);
        }

        foreach (var reference in module.AssemblyReferences)
        {
            Line(".assembly extern " + Identifier(reference.Name));
            Open();
            Line(".ver " + reference.Version.ToString().Replace('.', ':'));
            if (reference.PublicKeyToken.Length != 0)
            {
                Line(".publickeytoken = (" + Bytes(reference.PublicKeyToken) + ")");
            }

            if (!string.IsNullOrEmpty(reference.Culture))
            {
                Line(".locale " + LiteralParser.Escape(reference.Culture));
            }

            Close();
        }

        if (_draft is not null)
        {
            var existing = module.AssemblyReferences.Select(reference => reference.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in IlAsmRenderer.AssemblyReferences(_draft).Where(name => !existing.Contains(name)))
            {
                Line(".assembly extern " + Identifier(name) + " {}");
            }
        }

        Line(".assembly " + Identifier(_assembly));
        Open();
        Line(".ver " + module.Assembly.Name.Version.ToString().Replace('.', ':'));
        Attributes(module.Assembly);
        Close();
        Line(".module " + Identifier(_assembly + ".dll"));
        Attributes(module);
        foreach (var type in module.Types.Where(type => type.Name != "<Module>"))
        {
            WriteType(type);
        }
    }

    private void WriteType(TypeDefinition type)
    {
        var kind = type.IsInterface ? TypeKind.Interface : TypeKind.Class;
        var name = type.IsNested ? TypeNameFormatter.IlAsmTypeName(type.Name) : QualifiedName(type);
        Line(".class " + IlAsmWords.Type((ReflectionTypeAttributes)type.Attributes, kind, type.IsNested)
            + name + Generics(type));
        if (type.BaseType is { } parent)
        {
            Line("extends " + TypeText(parent));
        }

        if (type.HasInterfaces)
        {
            Line("implements " + string.Join(", ", type.Interfaces.Select(entry => TypeText(entry.InterfaceType))));
        }

        Open();
        Attributes(type);
        GenericAttributes(type);
        if (type.HasLayoutInfo)
        {
            Line(".pack " + Number(type.PackingSize));
            Line(".size " + Number(type.ClassSize));
        }

        foreach (var field in type.Fields)
        {
            WriteField(field);
        }

        foreach (var method in type.Methods)
        {
            WriteMethod(method);
        }

        WriteProperties(type);
        foreach (var nested in type.NestedTypes)
        {
            WriteType(nested);
        }

        if (_draft is not null && type.FullName == "IlRepl.Cell")
        {
            IlAsmRenderer.RenderCell(_text, _draft);
        }

        Close();
    }

    private void WriteField(FieldDefinition field)
    {
        var signature = MetadataSignatures.FieldOperand(_metadata, field.MetadataToken.ToInt32(), _signatures, GenericContext.Empty)!;
        var offset = field.HasLayoutInfo ? "[" + Number(field.Offset) + "] " : "";
        var data = field.InitialValue.Length == 0 ? "" : " at Data_" + field.MetadataToken.ToInt32().ToString("x8");
        Line(".field " + offset + IlAsmWords.Field((ReflectionFieldAttributes)field.Attributes) + Marshal(field.MetadataToken)
            + Signature(signature) + " " + Identifier(field.Name) + data + Constant(field));
        Attributes(field);
        if (field.InitialValue.Length != 0)
        {
            Line(".data Data_" + field.MetadataToken.ToInt32().ToString("x8") + " = bytearray (" + Bytes(field.InitialValue) + ")");
        }
    }

    private void WriteMethod(MethodDefinition method)
    {
        var signature = MetadataSignatures.MethodDefinition(_metadata, method.MetadataToken.ToInt32(), _signatures, GenericContext.Empty);
        var flags = (ReflectionMethodAttributes)method.Attributes & ~ReflectionMethodAttributes.PinvokeImpl;
        var parameters = method.Parameters.Select((parameter, index) => Parameter(parameter, signature.Parameters[index]));
        var pinvoke = method.PInvokeInfo is { } native ? "pinvokeimpl(" + LiteralParser.Escape(native.Module.Name)
            + " as " + LiteralParser.Escape(native.EntryPoint) + " flags(" + Number((int)native.Attributes) + ")) " : "";
        Line(".method " + IlAsmWords.Method(flags) + pinvoke + Convention(signature)
            + Parameter(method.MethodReturnType, signature.ReturnType) + " " + IlAsmRenderer.MemberName(method.Name) + Generics(method)
            + "(" + string.Join(", ", parameters) + ") flags(" + Number((int)method.ImplAttributes) + ")");
        Open();
        Attributes(method);
        GenericAttributes(method);
        ParameterAttributes(method.MethodReturnType, 0);
        foreach (var parameter in method.Parameters)
        {
            ParameterAttributes(parameter, parameter.Index + 1);
        }

        foreach (var over in method.Overrides)
        {
            Line(".override method " + MethodText(over));
        }

        if (method.HasBody)
        {
            WriteBody(method);
        }

        Close();
    }

    private void Line(string value) => _text.Append(' ', _indent * 4).AppendLine(value);

    private void Open()
    {
        Line("{");
        _indent++;
    }

    private void Close()
    {
        _indent--;
        Line("}");
    }

    private static string Number<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);

    private static string Identifier(string value) => TypeNameFormatter.IlAsmIdentifier(value);

    private static string Bytes(byte[] bytes) => string.Join(" ", bytes.Select(value => value.ToString("x2")));
}
