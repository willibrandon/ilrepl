using System.Reflection;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// The ILAsm words for metadata attributes, in the order ildasm writes them.
/// </summary>
public static class IlAsmWords
{
    /// <summary>
    /// The words of a <c>.class</c> header before the name, with a trailing space.
    /// </summary>
    /// <param name="attributes">The type attributes.</param>
    /// <param name="kind">The kind, which adds <c>interface</c>.</param>
    /// <param name="nested">True for a nested type.</param>
    /// <returns>The words.</returns>
    public static string Type(TypeAttributes attributes, TypeKind kind, bool nested)
    {
        var sb = new StringBuilder();
        if (kind == TypeKind.Interface)
        {
            sb.Append("interface ");
        }

        sb.Append(MemberAccess.VisibilityWord(attributes)).Append(' ');
        if (attributes.HasFlag(TypeAttributes.Abstract))
        {
            sb.Append("abstract ");
        }

        sb.Append((attributes & TypeAttributes.LayoutMask) switch
        {
            TypeAttributes.SequentialLayout => "sequential ",
            TypeAttributes.ExplicitLayout => "explicit ",
            _ => "auto ",
        });
        sb.Append((attributes & TypeAttributes.StringFormatMask) switch
        {
            TypeAttributes.UnicodeClass => "unicode ",
            TypeAttributes.AutoClass => "autochar ",
            _ => "ansi ",
        });
        if (attributes.HasFlag(TypeAttributes.Import))
        {
            sb.Append("import ");
        }

        if (((int)attributes & 0x2000) != 0)
        {
            sb.Append("serializable ");
        }

        if (attributes.HasFlag(TypeAttributes.Sealed))
        {
            sb.Append("sealed ");
        }

        if (attributes.HasFlag(TypeAttributes.SpecialName))
        {
            sb.Append("specialname ");
        }

        if (attributes.HasFlag(TypeAttributes.RTSpecialName))
        {
            sb.Append("rtspecialname ");
        }

        if (attributes.HasFlag(TypeAttributes.BeforeFieldInit))
        {
            sb.Append("beforefieldinit ");
        }

        _ = nested;
        return sb.ToString();
    }

    /// <summary>
    /// The words of a <c>.field</c> after the offset, with a trailing space.
    /// </summary>
    /// <param name="attributes">The field attributes.</param>
    /// <returns>The words.</returns>
    public static string Field(FieldAttributes attributes)
    {
        var sb = new StringBuilder();
        sb.Append(MemberAccess.AccessWord(attributes)).Append(' ');
        if (attributes.HasFlag(FieldAttributes.Static))
        {
            sb.Append("static ");
        }

        if (attributes.HasFlag(FieldAttributes.InitOnly))
        {
            sb.Append("initonly ");
        }

        if (attributes.HasFlag(FieldAttributes.Literal))
        {
            sb.Append("literal ");
        }

        if (((int)attributes & 0x80) != 0)
        {
            sb.Append("notserialized ");
        }

        if (attributes.HasFlag(FieldAttributes.SpecialName))
        {
            sb.Append("specialname ");
        }

        if (attributes.HasFlag(FieldAttributes.RTSpecialName))
        {
            sb.Append("rtspecialname ");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The words of a <c>.method</c> header before the calling convention, with a trailing space.
    /// </summary>
    /// <param name="attributes">The method attributes.</param>
    /// <returns>The words.</returns>
    public static string Method(MethodAttributes attributes)
    {
        var sb = new StringBuilder();
        sb.Append(MemberAccess.AccessWord(attributes)).Append(' ');
        if (attributes.HasFlag(MethodAttributes.HideBySig))
        {
            sb.Append("hidebysig ");
        }

        if (attributes.HasFlag(MethodAttributes.NewSlot))
        {
            sb.Append("newslot ");
        }

        if (attributes.HasFlag(MethodAttributes.SpecialName))
        {
            sb.Append("specialname ");
        }

        if (attributes.HasFlag(MethodAttributes.RTSpecialName))
        {
            sb.Append("rtspecialname ");
        }

        if (attributes.HasFlag(MethodAttributes.Abstract))
        {
            sb.Append("abstract ");
        }

        if (attributes.HasFlag(MethodAttributes.Virtual))
        {
            sb.Append("virtual ");
        }

        if (attributes.HasFlag(MethodAttributes.Final))
        {
            sb.Append("final ");
        }

        if (attributes.HasFlag(MethodAttributes.Static))
        {
            sb.Append("static ");
        }

        if (attributes.HasFlag(MethodAttributes.PinvokeImpl))
        {
            sb.Append("pinvokeimpl ");
        }

        if (attributes.HasFlag(MethodAttributes.UnmanagedExport))
        {
            sb.Append("unmanagedexp ");
        }

        if (attributes.HasFlag(MethodAttributes.CheckAccessOnOverride))
        {
            sb.Append("strict ");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The implementation words after the parameter list: <c>cil managed</c> and the flags.
    /// </summary>
    /// <param name="attributes">The implementation attributes.</param>
    /// <returns>The words.</returns>
    public static string Implementation(MethodImplAttributes attributes)
    {
        var sb = new StringBuilder("cil managed");
        if (attributes.HasFlag(MethodImplAttributes.NoInlining))
        {
            sb.Append(" noinlining");
        }

        if (attributes.HasFlag(MethodImplAttributes.NoOptimization))
        {
            sb.Append(" nooptimization");
        }

        if (attributes.HasFlag(MethodImplAttributes.Synchronized))
        {
            sb.Append(" synchronized");
        }

        if (attributes.HasFlag(MethodImplAttributes.AggressiveInlining))
        {
            sb.Append(" aggressiveinlining");
        }

        if (attributes.HasFlag(MethodImplAttributes.AggressiveOptimization))
        {
            sb.Append(" aggressiveoptimization");
        }

        if (attributes.HasFlag(MethodImplAttributes.PreserveSig))
        {
            sb.Append(" preservesig");
        }

        return sb.ToString();
    }
}
