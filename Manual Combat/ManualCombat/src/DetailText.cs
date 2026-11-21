// Manual Combat for Meitou. GPLv3 or later (see LICENSE).
//
// The detail lines (ManualCombatSystem.Detail) cost nothing while logging is off: a $"..." passed to Detail goes through this handler, which asks
// whether logging is on before anything is built. Off, the compiler skips every piece of the line: no text is made and no value in it is worked out.

using System.Runtime.CompilerServices;

namespace ManualCombat;

[InterpolatedStringHandler]
public ref struct DetailText
{
    DefaultInterpolatedStringHandler _text;

    /// <summary>Logging is on (Log details, or a test run): the line is being built.</summary>
    public readonly bool On;

    public DetailText(int literalLength, int formattedCount, ManualCombatSystem mc, out bool on)
    {
        On = on = mc.Logging;
        _text = on ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _text.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _text.AppendFormatted(value, format);

    /// <summary>The finished line.</summary>
    public string Text() => _text.ToStringAndClear();
}
