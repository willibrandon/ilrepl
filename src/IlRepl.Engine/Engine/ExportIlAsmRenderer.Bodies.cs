using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;

namespace IlRepl.Engine;

/// <summary>
/// Renders copied method bodies and their exact exception regions as ILAsm.
/// </summary>
internal sealed partial class ExportIlAsmRenderer
{
    private void WriteBody(MethodDefinition method)
    {
        var body = method.Body;
        var definition = _metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(method.MetadataToken.ToInt32()));
        var bytes = _pe.GetMethodBody(definition.RelativeVirtualAddress);
        var localToken = bytes.LocalSignature.IsNil ? 0 : MetadataTokens.GetToken(bytes.LocalSignature);
        var locals = MetadataSignatures.Locals(_metadata, localToken, _signatures, GenericContext.Empty)!;
        Line(".maxstack " + Number(Math.Max(body.MaxStackSize, !body.InitLocals && locals.Count == 0 ? 8 : 0)));
        if (locals.Count != 0)
        {
            Line((body.InitLocals ? ".locals init (" : ".locals (")
                + string.Join(", ", locals.Select((local, index) => "[" + Number(index) + "] " + Signature(local))) + ")");
        }
        else if (body.InitLocals)
        {
            Line(".zeroinit");
        }

        foreach (var instruction in body.Instructions)
        {
            var prefix = Label(instruction.Offset) + ": ";
            if (instruction.OpCode.Code == Code.No)
            {
                Line(prefix + ".emitbyte 254");
                Line(".emitbyte 25");
                Line(".emitbyte " + Number((byte)instruction.Operand));
            }
            else
            {
                var operand = Operand(instruction, method);
                Line(prefix + instruction.OpCode.Name + (operand.Length == 0 ? "" : " " + operand));
            }
        }

        Line(Label(body.CodeSize) + ":");
        foreach (var handler in body.ExceptionHandlers)
        {
            var kind = handler.HandlerType switch
            {
                ExceptionHandlerType.Catch => "catch " + TypeText(handler.CatchType),
                ExceptionHandlerType.Filter => "filter " + Label(handler.FilterStart.Offset),
                ExceptionHandlerType.Finally => "finally",
                ExceptionHandlerType.Fault => "fault",
                _ => throw new ReplException("the metadata contains an invalid exception handler"),
            };
            Line(".try " + Label(handler.TryStart.Offset) + " to " + Label(handler.TryEnd?.Offset ?? body.CodeSize)
                + " " + kind + " handler " + Label(handler.HandlerStart.Offset)
                + " to " + Label(handler.HandlerEnd?.Offset ?? body.CodeSize));
        }
    }

    private string Operand(CecilInstruction instruction, MethodDefinition owner) => instruction.Operand switch
    {
        null => "",
        CecilInstruction target => Label(target.Offset),
        CecilInstruction[] targets => "(" + string.Join(", ", targets.Select(target => Label(target.Offset))) + ")",
        VariableDefinition variable => Number(variable.Index),
        ParameterDefinition parameter => Number(parameter.Index + (owner.HasThis ? 1 : 0)),
        MethodReference method => (instruction.OpCode.Code == Code.Ldtoken ? "method " : "") + MethodText(method),
        FieldReference field => (instruction.OpCode.Code == Code.Ldtoken ? "field " : "")
            + Signature(MetadataSignatures.FieldOperand(_metadata, field.MetadataToken.ToInt32(), _signatures, GenericContext.Empty)!)
            + " " + TypeText(field.DeclaringType) + "::" + Identifier(field.Name),
        TypeReference type => TypeText(type),
        CallSite site => IlSignatureRenderer.IlAsm(Normalize(MetadataSignatures.StandaloneMethod(_metadata,
            site.MetadataToken.ToInt32(), _signatures, GenericContext.Empty)!)),
        string text => "bytearray (" + Bytes(Utf16(text)) + ")",
        float number => "(" + Bytes(BitConverter.GetBytes(number)) + ")",
        double number => "(" + Bytes(BitConverter.GetBytes(number)) + ")",
        IFormattable number => Number(number),
        _ => throw new ReplException("the metadata contains an invalid instruction operand"),
    };

    private void WriteProperties(TypeDefinition type)
    {
        foreach (var property in type.Properties)
        {
            var handle = (PropertyDefinitionHandle)MetadataTokens.EntityHandle(property.MetadataToken.ToInt32());
            var signature = _metadata.GetPropertyDefinition(handle).DecodeSignature(_signatures, GenericContext.Empty);
            Line(".property " + (property.IsSpecialName ? "specialname " : "") + (property.HasThis ? "instance " : "")
                + Signature(signature.ReturnType) + " " + Identifier(property.Name)
                + "(" + string.Join(", ", signature.ParameterTypes.Select(Signature)) + ")" + Constant(property));
            Open();
            Attributes(property);
            Accessor(".get", property.GetMethod);
            Accessor(".set", property.SetMethod);
            foreach (var method in property.OtherMethods)
            {
                Accessor(".other", method);
            }

            Close();
        }

        foreach (var entry in type.Events)
        {
            Line(".event " + (entry.IsSpecialName ? "specialname " : "") + TypeText(entry.EventType) + " " + Identifier(entry.Name));
            Open();
            Attributes(entry);
            Accessor(".addon", entry.AddMethod);
            Accessor(".removeon", entry.RemoveMethod);
            Accessor(".fire", entry.InvokeMethod);
            foreach (var method in entry.OtherMethods)
            {
                Accessor(".other", method);
            }

            Close();
        }
    }

    private void Accessor(string keyword, MethodDefinition? method)
    {
        if (method is not null)
        {
            Line(keyword + " " + MethodText(method));
        }
    }

    private static string Label(int offset) => "IL_" + offset.ToString("x4");

    private static byte[] Utf16(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            bytes[index * 2] = (byte)text[index];
            bytes[index * 2 + 1] = (byte)(text[index] >> 8);
        }

        return bytes;
    }
}
