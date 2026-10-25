using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Yalla.UnitTests.Architecture;

/// <summary>
/// The methods a method body calls, read from its IL (<see cref="MethodBody.GetILAsByteArray"/>) with the method tokens
/// resolved through its module - so a rule about what code calls holds for the compiled code, not for text in a file.
/// </summary>
/// <remarks>
/// <para>
/// Every instruction with a method operand counts: <c>call</c>, <c>callvirt</c>, <c>newobj</c>, and <c>ldftn</c> /
/// <c>ldvirtftn</c>, which is how a lambda or local function becomes a delegate. A token that does not resolve (a
/// generic context reflection cannot rebuild) is skipped rather than failing the read; the rules here name concrete,
/// non-generic targets.
/// </para>
/// <para>
/// <b>What the compiler moved out of the method</b> is followed by <see cref="BodiesOf"/>: an <c>async</c> method's
/// body is its state machine's <c>MoveNext</c>, an iterator's likewise, and a lambda is reached through its
/// <c>ldftn</c>.
/// </para>
/// </remarks>
internal static class MethodCalls
{
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static MethodCalls()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var code = (OpCode)field.GetValue(null)!;
            var value = (ushort)code.Value;

            if (value < 0x100)
            {
                OneByte[value] = code;
            }
            else if ((value & 0xff00) == 0xfe00)
            {
                TwoByte[value & 0xff] = code;
            }
        }
    }

    /// <summary>Every method and constructor the type itself declares, static ones included.</summary>
    public static IEnumerable<MethodBase> DeclaredBy(Type type) =>
        type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));

    /// <summary>
    /// The method itself, and the compiler-generated method that holds its body when it is <c>async</c> or an iterator.
    /// </summary>
    public static IEnumerable<MethodBase> BodiesOf(MethodBase method)
    {
        yield return method;

        var machine = method.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType;

        if (machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } moveNext)
        {
            yield return moveNext;
        }
    }

    /// <summary>The methods <paramref name="method"/>'s own IL calls, constructs, or makes a delegate of.</summary>
    public static IEnumerable<MethodBase> CalledBy(MethodBase method)
    {
        byte[]? il;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (InvalidOperationException)
        {
            il = null;
        }

        if (il is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } ? method.GetGenericArguments() : null;

        for (var at = 0; at < il.Length;)
        {
            var code = il[at] == 0xfe ? TwoByte[il[++at]] : OneByte[il[at]];
            at++;

            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, at);

                if (Resolve(method.Module, token, typeArguments, methodArguments) is { } target)
                {
                    yield return target;
                }
            }

            at += OperandSize(code, il, at);
        }
    }

    /// <summary>
    /// Every method reachable from <paramref name="roots"/> through calls into Yalla's own assemblies, with the path
    /// that reaches it - for a rule that a whole call tree must keep (a handler never saves, however deep the save).
    /// Calls into anything else (the framework, EF Core) are reported but not entered.
    /// </summary>
    public static IEnumerable<(MethodBase Called, IReadOnlyList<MethodBase> Path)> ReachableFrom(IEnumerable<MethodBase> roots)
    {
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<(MethodBase Method, IReadOnlyList<MethodBase> Path)>();

        foreach (var root in roots)
        {
            if (seen.Add(root))
            {
                queue.Enqueue((root, [root]));
            }
        }

        while (queue.Count > 0)
        {
            var (method, path) = queue.Dequeue();

            foreach (var body in BodiesOf(method))
            {
                foreach (var called in CalledBy(body))
                {
                    IReadOnlyList<MethodBase> to = [.. path, called];

                    yield return (called, to);

                    var definition = called is MethodInfo { IsGenericMethod: true } generic
                        ? generic.GetGenericMethodDefinition()
                        : called;

                    if (ProductionCode.IsYalla(definition.Module.Assembly) && seen.Add(definition))
                    {
                        queue.Enqueue((definition, to));
                    }
                }
            }
        }
    }

    /// <summary>A call path as a reader follows it: <c>A.M -> B.N -> C.O</c>.</summary>
    public static string Describe(IEnumerable<MethodBase> path) => string.Join(" -> ", path.Select(ProductionCode.NameOf));

    private static MethodBase? Resolve(Module module, int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        try
        {
            return module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static int OperandSize(OpCode code, byte[] il, int at) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
        _ => 4,
    };
}
