namespace Cmux.Core.Terminal;

/// <summary>
/// Width classification for BMP terminal characters.
/// </summary>
public static class TerminalWidth
{
    public static int GetWidth(char ch)
    {
        if (ch == '\0')
            return 0;

        if (char.IsControl(ch) || IsCombining(ch))
            return 0;

        return IsWide(ch) ? 2 : 1;
    }

    public static bool IsWide(char ch)
    {
        var code = ch;

        return code >= 0x1100 && (
            code <= 0x115F ||
            code == 0x2329 ||
            code == 0x232A ||
            code is >= (char)0x2E80 and <= (char)0xA4CF and not (char)0x303F ||
            code is >= (char)0xAC00 and <= (char)0xD7A3 ||
            code is >= (char)0xF900 and <= (char)0xFAFF ||
            code is >= (char)0xFE10 and <= (char)0xFE19 ||
            code is >= (char)0xFE30 and <= (char)0xFE6F ||
            code is >= (char)0xFF00 and <= (char)0xFF60 ||
            code is >= (char)0xFFE0 and <= (char)0xFFE6);
    }

    private static bool IsCombining(char ch)
    {
        var code = ch;

        return code is >= (char)0x0300 and <= (char)0x036F
            or >= (char)0x0483 and <= (char)0x0489
            or >= (char)0x0591 and <= (char)0x05BD
            or (char)0x05BF
            or >= (char)0x05C1 and <= (char)0x05C2
            or >= (char)0x05C4 and <= (char)0x05C5
            or (char)0x05C7
            or >= (char)0x0610 and <= (char)0x061A
            or >= (char)0x064B and <= (char)0x065F
            or (char)0x0670
            or >= (char)0x06D6 and <= (char)0x06DC
            or >= (char)0x06DF and <= (char)0x06E4
            or >= (char)0x06E7 and <= (char)0x06E8
            or >= (char)0x06EA and <= (char)0x06ED
            or >= (char)0x0711 and <= (char)0x0730
            or >= (char)0x0732 and <= (char)0x074A
            or >= (char)0x07A6 and <= (char)0x07B0
            or >= (char)0x07EB and <= (char)0x07F3
            or >= (char)0x0816 and <= (char)0x0819
            or >= (char)0x081B and <= (char)0x0823
            or >= (char)0x0825 and <= (char)0x0827
            or >= (char)0x0829 and <= (char)0x082D
            or >= (char)0x0859 and <= (char)0x085B
            or >= (char)0x08D3 and <= (char)0x08E1
            or >= (char)0x08E3 and <= (char)0x0903
            or >= (char)0x093A and <= (char)0x093C
            or >= (char)0x0941 and <= (char)0x0948
            or (char)0x094D
            or >= (char)0x0951 and <= (char)0x0957
            or >= (char)0x0962 and <= (char)0x0963
            or >= (char)0x0981 and <= (char)0x0983
            or (char)0x09BC
            or >= (char)0x09C1 and <= (char)0x09C4
            or (char)0x09CD
            or (char)0x09E2
            or (char)0x09E3
            or >= (char)0x0A01 and <= (char)0x0A03
            or (char)0x0A3C
            or >= (char)0x0A41 and <= (char)0x0A42
            or >= (char)0x0A47 and <= (char)0x0A48
            or >= (char)0x0A4B and <= (char)0x0A4D
            or >= (char)0x0A70 and <= (char)0x0A71
            or (char)0x0A75
            or >= (char)0x0A81 and <= (char)0x0A83
            or (char)0x0ABC
            or >= (char)0x0AC1 and <= (char)0x0AC5
            or >= (char)0x0AC7 and <= (char)0x0AC8
            or (char)0x0ACD
            or >= (char)0x0AE2 and <= (char)0x0AE3
            or >= (char)0x0B01 and <= (char)0x0B03
            or (char)0x0B3C
            or (char)0x0B3F
            or >= (char)0x0B41 and <= (char)0x0B44
            or (char)0x0B4D
            or >= (char)0x0B56 and <= (char)0x0B57
            or >= (char)0x0B62 and <= (char)0x0B63
            or (char)0x0B82
            or (char)0x0BC0
            or (char)0x0BCD
            or >= (char)0x0C00 and <= (char)0x0C04
            or >= (char)0x0C3E and <= (char)0x0C40
            or >= (char)0x0C46 and <= (char)0x0C48
            or >= (char)0x0C4A and <= (char)0x0C4D
            or >= (char)0x0C55 and <= (char)0x0C56
            or >= (char)0x0C62 and <= (char)0x0C63
            or >= (char)0x0C81 and <= (char)0x0C83
            or (char)0x0CBC
            or (char)0x0CBF
            or (char)0x0CC6
            or >= (char)0x0CCC and <= (char)0x0CCD
            or >= (char)0x0CE2 and <= (char)0x0CE3
            or >= (char)0x0D00 and <= (char)0x0D03
            or (char)0x0D3B
            or (char)0x0D3C
            or (char)0x0D41
            or (char)0x0D42
            or (char)0x0D4D
            or >= (char)0x0D62 and <= (char)0x0D63
            or >= (char)0x0DCA and <= (char)0x0DDF
            or (char)0x200C
            or (char)0x200D
            or >= (char)0x20D0 and <= (char)0x20FF
            or >= (char)0xFE00 and <= (char)0xFE0F
            or >= (char)0xFE20 and <= (char)0xFE2F;
    }
}
