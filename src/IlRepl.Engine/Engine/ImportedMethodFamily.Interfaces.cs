namespace IlRepl.Engine;

/// <summary>
/// Retains the nominal types that an unchanged external base uses to implement its interfaces.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private readonly Dictionary<Type, Type[]> _externalBases = [];
    private readonly HashSet<Type> _externalTypes = [];

    private bool ShouldCopyType(Type type, Type from) => !_externalTypes.Contains(type)
        && (TypeRelations.IsSessionType(type) || type.Assembly == from.Assembly && !type.IsVisible);

    private void PreserveExternalBase(Type type)
    {
        if (!_externalBases.TryAdd(type, type.GetInterfaces()))
        {
            return;
        }

        RetainExternalType(type);
        foreach (var contract in _externalBases[type])
        {
            RetainExternalType(contract);
        }
    }

    private void RetainExternalType(Type type)
    {
        if (type.HasElementType)
        {
            RetainExternalType(type.GetElementType()!);
            return;
        }

        if (type.IsGenericParameter)
        {
            return;
        }

        if (type.IsConstructedGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                RetainExternalType(argument);
            }
        }

        _externalTypes.Add(DefinitionOf(type));
    }

    private bool RefreshExternalBases()
    {
        var copied = _externalBases.Keys.Where(type => _types.ContainsKey(DefinitionOf(type))).ToArray();
        if (copied.Length == 0)
        {
            return false;
        }

        foreach (var type in copied)
        {
            _externalBases.Remove(type);
        }

        _externalTypes.Clear();
        foreach (var (type, contracts) in _externalBases)
        {
            RetainExternalType(type);
            foreach (var contract in contracts)
            {
                RetainExternalType(contract);
            }
        }

        return true;
    }

    private void ValidateExternalInterfaces()
    {
        foreach (var (type, contracts) in _externalBases)
        {
            foreach (var contract in contracts.Prepend(type))
            {
                if (ContainsCopiedType(contract))
                {
                    throw new ReplException($"external base {TypeNameFormatter.Pretty(type)} requires the original nominal type "
                        + $"{TypeNameFormatter.Pretty(contract)}; the copy has a distinct identity");
                }

                if (contract != type)
                {
                    ReportType(contract, TypeNameFormatter.Pretty(type) + ": external base contract");
                }
            }
        }
    }
}
