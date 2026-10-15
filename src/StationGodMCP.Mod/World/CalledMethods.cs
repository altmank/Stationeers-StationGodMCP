#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The methods a method's IL calls directly (call, callvirt, newobj, ldftn, ldvirtftn): the game's, the runtime's and
/// closed generic ones, which compiling the mod's own methods does not compile. Read from the method body's bytes with
/// the opcode table; a token that does not resolve is passed over. Main thread (LoadWarmup).
/// </summary>
internal static class CalledMethods
{
    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];
    private static bool _tables;

    /// <summary>Adds to called every method body's direct callee that can be compiled ahead.</summary>
    internal static void Of(MethodBase method, List<MethodBase> called)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return;
        }

        Tables();
        Module module = method.Module;
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int at = 0;
        while (at < il.Length)
        {
            OpCode code = il[at] == 0xFE && at + 1 < il.Length ? TwoByte[il[++at]] : OneByte[il[at]];
            at++;
            if (code.OperandType == OperandType.InlineMethod && at + 4 <= il.Length)
            {
                int token = BitConverter.ToInt32(il, at);
                MethodBase? callee = Resolve(module, token, typeArguments, methodArguments);
                if (callee != null && Compilable(callee))
                {
                    called.Add(callee);
                }
            }

            at += OperandSize(code, il, at);
        }
    }

    /// <summary>A method with a body and no open generic parameter.</summary>
    internal static bool Compilable(MethodBase method) =>
        !method.IsAbstract && !method.ContainsGenericParameters &&
        !(method.DeclaringType is { ContainsGenericParameters: true }) &&
        (method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0 &&
        (method.Attributes & MethodAttributes.PinvokeImpl) == 0;

    private static MethodBase? Resolve(Module module, int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        try
        {
            return module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (Exception)
        {
            // A member this game's build lacks, or a token the runtime will not resolve here: its first call compiles it.
            return null;
        }
    }

    private static int OperandSize(OpCode code, byte[] il, int at) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => at + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, at) : il.Length,
        _ => 4
    };

    // The opcode table by value, from the runtime's own list of opcodes.
    private static void Tables()
    {
        if (_tables)
        {
            return;
        }

        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode code)
            {
                ushort value = (ushort)code.Value;
                if (value < 0x100)
                {
                    OneByte[value] = code;
                }
                else if ((value & 0xFF00) == 0xFE00)
                {
                    TwoByte[value & 0xFF] = code;
                }
            }
        }

        _tables = true;
    }
}
