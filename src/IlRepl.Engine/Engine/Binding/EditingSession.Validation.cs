namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private void RecheckBody(EditingBody original)
    {
        var previous = _state.Method;
        var body = new EditingBody
        {
            Signature = original.Signature,
            Header = original.Header,
            Generics = original.Generics,
            Access = original.Access,
            ThisIndex = original.ThisIndex,
            BraceSeen = original.Header?.TrimEnd().EndsWith('{') == true,
            IsVarArg = original.IsVarArg,
            LabelSpace = original.LabelSpace,
        };
        body.Arguments.AddRange(original.Arguments);
        _state.Method = body;
        try
        {
            foreach (var line in original.Lines)
            {
                AddBodyLine(line);
            }

            RequireResolvedLabels(body);
            if (!body.EndsFlow && body.Signature is not { IsAbstract: true })
            {
                ValidateReturn(body, Scope());
            }
        }
        finally
        {
            _state.Method = previous;
        }
    }
}
