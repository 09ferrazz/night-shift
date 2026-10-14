using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace NightShift.App;

// Native selectable text; the transcript itself is never transformed or shortened.
public sealed class MessageBody : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MessageBody), new PropertyMetadata("", (d, _) => ((MessageBody)d).Render()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private static readonly Regex Fences = new(@"^```([^\r\n]*)\r?\n([\s\S]*?)^```[ \t]*(?:\r?\n|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private readonly List<TextBox> _textBoxes = [];

    private void Render()
    {
        var text = Text ?? "";
        Match[] matches = [];
        if (text.Length <= 64000)
        {
            try { matches = Fences.Matches(text).Cast<Match>().Take(65).ToArray(); }
            catch (RegexMatchTimeoutException) { }
        }
        if (matches.Length == 0 || matches.Length > 64 || matches.Any(m => m.Groups[1].Length > 40))
        {
            if (Children.Count == 1 && Children[0] is TextBox existing) { existing.Text = text; return; }
            Children.Clear(); _textBoxes.Clear(); Children.Add(CreateText(text)); return;
        }
        var selections = _textBoxes.Select(t => (t.SelectionStart, t.SelectionLength, t.IsKeyboardFocused)).ToArray();
        Children.Clear(); _textBoxes.Clear();
        var offset = 0;
        foreach (var match in matches)
        {
            if (match.Index > offset) Children.Add(CreateText(text[offset..match.Index]));
            var code = match.Groups[2].Value.TrimEnd('\r', '\n');
            var section = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(12,8,12,8) };
            var copy = new Button { Content = "Copiar", Style = (Style)FindResource("Quiet"), Padding = new Thickness(6,2,6,2), FontSize = 12, IsEnabled = code.Length > 0 };
            AutomationProperties.SetName(copy, "Copiar código");
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(code); }
                catch (System.Runtime.InteropServices.ExternalException) { copy.ToolTip = "Área de transferência ocupada. Tente novamente."; }
            };
            DockPanel.SetDock(copy, Dock.Right); header.Children.Add(copy);
            var language = new TextBlock { Text = string.IsNullOrWhiteSpace(match.Groups[1].Value) ? "Código" : match.Groups[1].Value.Trim(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            language.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); header.Children.Add(language); section.Children.Add(header);
            var body = CreateText(code); body.FontFamily = new FontFamily("Consolas"); body.SetResourceReference(TextBox.ForegroundProperty,"CodeInk"); body.Margin = new Thickness(14,8,14,14); section.Children.Add(body);
            var border = new Border { Child = section, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0,8,0,12) };
            border.SetResourceReference(Border.BackgroundProperty,"SurfaceAlt"); border.SetResourceReference(Border.BorderBrushProperty,"Line"); Children.Add(border);
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) Children.Add(CreateText(text[offset..]));
        for (int i = 0; i < Math.Min(selections.Length,_textBoxes.Count); i++)
        {
            var (start,length,focused) = selections[i];
            if (length > 0) _textBoxes[i].Select(Math.Min(start,_textBoxes[i].Text.Length),length);
            if (focused) _textBoxes[i].Focus();
        }
    }
    private TextBox CreateText(string text)
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontSize = 15, Template = new ControlTemplate(typeof(TextBox)) { VisualTree = host }
        };
        AutomationProperties.SetName(box, "Texto da mensagem"); _textBoxes.Add(box); return box;
    }
}
