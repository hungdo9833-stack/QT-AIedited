using System.Globalization;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

internal static class ChineseNumberParser
{
    private static readonly Dictionary<Rune, int> Digits = new()
    {
        [new('零')] = 0, [new('〇')] = 0, [new('○')] = 0,
        [new('一')] = 1, [new('二')] = 2, [new('两')] = 2, [new('兩')] = 2,
        [new('三')] = 3, [new('四')] = 4, [new('五')] = 5, [new('六')] = 6,
        [new('七')] = 7, [new('八')] = 8, [new('九')] = 9
    };

    public static bool IsNumberCharacter(char value) => value is >= '0' and <= '9' or >= '０' and <= '９' or
        '零' or '〇' or '○' or '一' or '二' or '两' or '兩' or '三' or '四' or '五' or '六' or '七' or '八' or '九' or
        '十' or '百' or '千' or '万' or '萬' or '亿' or '億' or '廿' or '卅';

    public static bool TryParse(string text, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string compact = string.Concat(text.Where(x => !char.IsWhiteSpace(x)));
        if (compact.Length == 0) return false;

        if (compact.All(x => x is >= '0' and <= '9' or >= '０' and <= '９'))
        {
            string ascii = string.Create(compact.Length, compact, static (span, value) =>
            {
                for (int i = 0; i < value.Length; i++) span[i] = value[i] is >= '０' and <= '９' ? (char)('0' + value[i] - '０') : value[i];
            });
            return int.TryParse(ascii, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
        }

        bool hasUnit = compact.Any(x => x is '十' or '百' or '千' or '万' or '萬' or '亿' or '億' or '廿' or '卅');
        if (!hasUnit)
        {
            long value = 0;
            foreach (Rune rune in compact.EnumerateRunes())
            {
                if (!Digits.TryGetValue(rune, out int digit)) return false;
                value = checked(value * 10 + digit);
                if (value > int.MaxValue) return false;
            }
            number = (int)value;
            return number > 0;
        }

        try
        {
            long total = 0, section = 0, current = 0;
            foreach (char value in compact)
            {
                if (value is '廿' or '卅')
                {
                    section += value == '廿' ? 20 : 30;
                    current = 0;
                    continue;
                }
                var rune = new Rune(value);
                if (Digits.TryGetValue(rune, out int digit)) { current = digit; continue; }
                long unit = value switch { '十' => 10, '百' => 100, '千' => 1000, '万' or '萬' => 10_000, '亿' or '億' => 100_000_000, _ => 0 };
                if (unit == 0) return false;
                if (unit < 10_000)
                {
                    if (current == 0) current = 1;
                    section = checked(section + current * unit);
                }
                else
                {
                    section = checked(section + current);
                    if (section == 0) section = 1;
                    total = checked(total + section * unit);
                    section = 0;
                }
                current = 0;
            }
            long result = checked(total + section + current);
            if (result <= 0 || result > int.MaxValue) return false;
            number = (int)result;
            return true;
        }
        catch (OverflowException) { return false; }
    }
}
