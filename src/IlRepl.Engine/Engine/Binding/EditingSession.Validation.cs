using IlRepl.Protocol;

namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private AnalysisLocation? _replayLocation;

    private void RecheckBody(EditingBody original)
    {
        var previous = _state.Method;
        var previousLocation = _replayLocation;
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
            var position = 0;
            foreach (var line in original.Lines)
            {
                var node = position < original.FlowNodes.Count ? original.FlowNodes[position] : null;
                _replayLocation = node?.Source == line ? node.Location : null;
                if (_replayLocation is not null)
                {
                    position++;
                }

                AddBodyLine(line);
            }

            if (_analyzingDocument)
            {
                foreach (var node in original.FlowNodes.Where(node => node.Synthetic))
                {
                    AddFlowNode(body, node, Scope());
                }

                return;
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
            _replayLocation = previousLocation;
        }
    }
}
