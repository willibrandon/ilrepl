using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Reconstructs a cell's metadata context from unchanged native-inspection images.
/// </summary>
public sealed partial class Session
{
    private readonly List<SessionMethod> _hiddenNativeBindings = [];
    /// <summary>
    /// Binds historical callables that remain reachable but are no longer visible session declarations.
    /// </summary>
    internal void ActivateNativeBindings()
    {
        foreach (var method in _hiddenNativeBindings)
        {
            method.Trampoline.Bind(method.Version.Implementation);
        }
    }

    /// <summary>
    /// Restores aliases and stable call bindings without replaying declarations or earlier execution.
    /// </summary>
    /// <param name="target">The captured declaration boundary.</param>
    /// <param name="context">The isolated image resolver.</param>
    internal void RestoreNative(NativeTarget target, NativeLoadContext context)
    {
        DeferActivation = true;
        foreach (var (name, type) in target.Types)
        {
            _typeTable.Add(name, context.ResolveType(type));
        }

        foreach (var (name, method) in target.Aliases)
        {
            _typeTable.MethodAliases.Add(name, context.ResolveMethod(method));
        }

        foreach (var binding in target.Bindings)
        {
            var trampolineMethod = (MethodInfo)context.ResolveMethod(binding.Trampoline);
            var body = (MethodInfo)context.ResolveMethod(binding.Implementation);
            var signature = new MethodSignature(binding.Name, body.ReturnType,
                [.. body.GetParameters().Select(parameter => new ArgumentDeclaration(parameter.ParameterType, parameter.Name, null, "")
                {
                    RequiredModifiers = parameter.GetRequiredCustomModifiers(), OptionalModifiers = parameter.GetOptionalCustomModifiers(),
                    Attributes = parameter.Attributes,
                })])
            {
                Attributes = body.Attributes, ImplAttributes = body.MethodImplementationFlags, CallingConvention = body.CallingConvention,
                ReturnRequiredModifiers = body.ReturnParameter.GetRequiredCustomModifiers(),
                ReturnOptionalModifiers = body.ReturnParameter.GetOptionalCustomModifiers(),
            };

            var trampoline = MethodTrampoline.Restore(signature, trampolineMethod);
            if (!SessionAssemblies.TryGetDefinition(body.Module.Assembly, out var definition))
            {
                throw new ReplException("captured implementation has no retained image");
            }

            var restored = new SessionMethod(signature, "", [], new CellState(Resolver, GenericContext.Empty), trampoline,
                new CompiledMethodVersion(definition, body, trampoline.DelegateType));
            if (binding.Visible)
            {
                _methods.Add(restored);
                InvalidateSignatures();
            }
            else
            {
                _hiddenNativeBindings.Add(restored);
            }
        }

        Rebuild();
        if (target.Cell is not { } cell)
        {
            return;
        }

        foreach (var line in cell.Declarations.Concat(cell.Body))
        {
            AddLine(line);
        }

        if (cell.TypeArguments.Length != 0)
        {
            TypeArguments = [.. cell.TypeArguments.Select(context.ResolveType)];
        }
    }
}
