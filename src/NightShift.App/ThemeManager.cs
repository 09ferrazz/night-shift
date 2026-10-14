using System.Windows;
using System.Windows.Media;
using NightShift.Core;

namespace NightShift.App;

internal static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static void Apply(bool dark, AgentProvider provider = AgentProvider.Codex)
    {
        IsDark = dark;
        var colors = new (string Key, string Dark, string Light)[]
        {
            ("HoverSurface", "#223A48", "#E6F0F4"), ("PressedSurface", "#2B4858", "#D4E5EC"),
            ("ActionHover", "#3E718A", "#28657E"), ("ActionPressed", "#295168", "#19475D"),
            ("SidebarBg", "#0D1922", "#EAF1F4"), ("SidebarSurface", "#152632", "#F7FAFC"),
            ("SidebarLine", "#263A47", "#CCDCE4"), ("SidebarInk", "#E4EEF3", "#183548"),
            ("SidebarMuted", "#99AFBD", "#536D7C"), ("SidebarSelected", "#223C4C", "#D5E5ED"),
            ("SidebarHover", "#1C3240", "#DFEBF1"), ("SidebarPressed", "#2B4656", "#C9DDE7"),
            ("SidebarAccent", "#B6DEE9", "#23566E"),
            ("Bg", "#111F2B", "#F5FAFC"), ("Surface", "#192C3A", "#FFFFFF"),
            ("SurfaceAlt", "#142431", "#EDF3F6"), ("Line", "#304755", "#CBDCE5"),
            ("Ink", "#ECF3F7", "#183345"), ("Muted", "#A6BAC7", "#4C6575"),
            // Keep the existing resource names so dialogs and message rendering share the new palette.
            ("Purple", "#91CDE2", "#28657E"), ("Mint", "#9FD6D6", "#26696C"),
            ("Action", "#305F77", "#23566F"), ("UserSurface", "#1C3342", "#E6F0F5"),
            ("NoticeSurface", "#343128", "#FFF3DA"), ("NoticeInk", "#F1D49B", "#704F16"),
            ("CodeInk", "#C0DEED", "#26546C"), ("Selection", "#365B70", "#BEDCE9"),
            ("StatusSurface", "#19363E", "#E0EFF0"), ("Disabled", "#20323F", "#E5ECF0"),
            ("BrandInk", "#C0E2ED", "#234962"), ("AvatarSurface", "#234152", "#D6E7EF")
        };
        if (provider == AgentProvider.Claude)
        {
            var warm = new Dictionary<string, (string Dark, string Light)>
            {
                ["Bg"] = ("#211F1D", "#FAF6F1"), ["Surface"] = ("#2B2825", "#FFFCF8"),
                ["SurfaceAlt"] = ("#24211F", "#F2EAE1"), ["Line"] = ("#51443D", "#DCCBC0"),
                ["Ink"] = ("#F6EEE7", "#382A23"), ["Muted"] = ("#C4B3A6", "#665247"),
                ["Purple"] = ("#D58A70", "#A85C43"), ["Mint"] = ("#DFC2AB", "#85533E"),
                ["Action"] = ("#8D4A34", "#994D34"), ["ActionHover"] = ("#A55B42", "#803C28"),
                ["ActionPressed"] = ("#703822", "#663020"), ["HoverSurface"] = ("#3C312B", "#EADACD"),
                ["PressedSurface"] = ("#514036", "#DFCBBC"), ["UserSurface"] = ("#392C26", "#F2E2D5"),
                ["NoticeSurface"] = ("#3D3326", "#FFF0D6"), ["NoticeInk"] = ("#F2D29B", "#704C13"),
                ["CodeInk"] = ("#E8B9A3", "#8D452D"), ["Selection"] = ("#684735", "#E6C8B5"),
                ["StatusSurface"] = ("#3B3028", "#F0DED0"), ["Disabled"] = ("#302A25", "#EEE5DC"),
                ["AvatarSurface"] = ("#4A352A", "#ECD4C3")
            };
            colors = colors.Select(c => warm.TryGetValue(c.Key, out var value) ? (c.Key, value.Dark, value.Light) : c).ToArray();
        }
        foreach (var (key, night, day) in colors)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : day));
    }
}
