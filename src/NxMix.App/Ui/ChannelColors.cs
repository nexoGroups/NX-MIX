using Vortice.Mathematics;

namespace NxMix.App.Ui;

internal static class ChannelColors
{
    private static readonly (string Name, uint Dark, uint Light)[] Palette =
    [
        ("Rot", 0xFF717A, 0xB42B3D),
        ("Orange", 0xFF9D61, 0x9E490E),
        ("Gelb", 0xF6CE60, 0x7B5A00),
        ("Grün", 0x80D888, 0x24692B),
        ("Türkis", 0x56D9CB, 0x006E63),
        ("Blau", 0x7EBAFF, 0x245FA3),
        ("Violett", 0xBAA0FF, 0x663DAA),
        ("Pink", 0xF58ED2, 0xA22A78),
    ];

    public static Color4? Get(int? index) => index is int i && (uint)i < Palette.Length
        ? Theme.Rgb(Theme.IsDark ? Palette[i].Dark : Palette[i].Light)
        : null;

    public static List<MenuItem> Menu(int? current, Action<int?> select)
    {
        var items = new List<MenuItem>
        {
            MenuItem.Header("Kanalfarbe"),
            new("Standard", null, () => select(null)) { Checked = Get(current) == null },
        };
        for (int i = 0; i < Palette.Length; i++)
        {
            int index = i;
            items.Add(new MenuItem(Palette[i].Name, null, () => select(index))
            {
                Swatch = Get(index),
                Checked = current == index,
            });
        }
        return items;
    }
}
