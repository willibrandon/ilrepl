using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Maps runtime calling-convention marker identities to the corresponding unmanaged convention.
/// </summary>
internal static class FunctionPointerConvention
{
    /// <summary>
    /// Reads the primary convention while allowing additional markers such as suppressed GC transitions.
    /// </summary>
    /// <param name="markers">The convention marker types.</param>
    /// <returns>The named convention, or the platform default when no primary marker is present.</returns>
    public static CallingConvention FromMarkers(IEnumerable<TypeSymbol> markers)
    {
        foreach (var marker in markers)
        {
            if (SymbolIdentity.Equal(marker, RuntimeSymbolImporter.Import(typeof(CallConvCdecl))))
            {
                return CallingConvention.Cdecl;
            }

            if (SymbolIdentity.Equal(marker, RuntimeSymbolImporter.Import(typeof(CallConvStdcall))))
            {
                return CallingConvention.StdCall;
            }

            if (SymbolIdentity.Equal(marker, RuntimeSymbolImporter.Import(typeof(CallConvThiscall))))
            {
                return CallingConvention.ThisCall;
            }

            if (SymbolIdentity.Equal(marker, RuntimeSymbolImporter.Import(typeof(CallConvFastcall))))
            {
                return CallingConvention.FastCall;
            }
        }

        return CallingConvention.Winapi;
    }
}
