using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NightShift.Core;

namespace NightShift.App;

public sealed class RequestWindow : Window
{
    private readonly PendingRequest _request;
    private readonly List<(string Id, Func<string> Value)> _questions = [];
    private readonly List<(string Id, Func<object?> Value, bool Required)> _form = [];
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    public object? Result { get; private set; }
    public RequestWindow(PendingRequest request)
    {
        Style = (Style)FindResource("AppWindow");
        _error.SetResourceReference(TextBlock.ForegroundProperty,"NoticeInk");
        Loaded += (_, _) => NativeMethods.DarkTitlebar(new System.Windows.Interop.WindowInteropHelper(this).Handle,ThemeManager.IsDark);
        _request = request; Title = "Night Shift — " + request.Title; Width = 680; Height = 620; MinWidth = 500; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24) }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = request.Title, FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = "Sua resposta vale somente para esta solicitação.", Foreground = (Brush)FindResource("Muted"), Margin = new(0, 8, 0, 18) }); root.Children.Add(heading);
        var content = new StackPanel(); var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        content.Children.Add(new TextBox { Text = request.Details, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 16), FontSize = 14 });
        if ((request.Method is "item/tool/requestUserInput" or "claude/question") && request.Parameters.TryGetProperty("questions", out var questions))
        {
            foreach (var q in questions.EnumerateArray()) AddQuestion(content, q);
        }
        if (request.Method == "mcpServer/elicitation/request" && request.Parameters.TryGetProperty("requestedSchema", out var schema)
            && schema.TryGetProperty("properties", out var properties))
        {
            var required = schema.TryGetProperty("required", out var fields) ? fields.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
            foreach (var field in properties.EnumerateObject()) AddFormField(content, field.Name, field.Value, required.Contains(field.Name));
        }
        content.Children.Add(_error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) }; Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        var close = new Button { Content = "Deixar pendente", Margin = new(0, 0, 10, 0) }; close.Click += (_, _) => Close(); buttons.Children.Add(close);
        var decline = new Button { Content = "Recusar", Margin = new(0, 0, 10, 0) }; decline.Click += (_, _) => Finish(false); buttons.Children.Add(decline);
        var accept = new Button { Content = request.Method is "item/tool/requestUserInput" or "claude/question" ? "Enviar resposta" : "Permitir uma vez", Style = (Style)FindResource("Primary") };
        accept.Click += (_, _) => Finish(true); buttons.Children.Add(accept);
        var supported = new[] { "item/commandExecution/requestApproval", "item/fileChange/requestApproval", "item/tool/requestUserInput", "item/permissions/requestApproval", "mcpServer/elicitation/request", "claude/permission", "claude/question" };
        if (!supported.Contains(request.Method)) { accept.IsEnabled = false; _error.Text = "Este tipo de pedido ainda não é suportado. Recusar informa isso à IA."; }
        if (request.Parameters.TryGetProperty("availableDecisions", out var decisions) && decisions.ValueKind == JsonValueKind.Array
            && !decisions.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.String && d.GetString() == "accept")
            && request.Method == "item/commandExecution/requestApproval") accept.IsEnabled = false;
        Content = root;
    }
    private void AddQuestion(Panel content, JsonElement q)
    {
        var id = _request.Method == "claude/question" ? CodexSession.Text(q, "question") ?? "" : CodexSession.Text(q, "id") ?? "";
        content.Children.Add(new TextBlock { Text = CodexSession.Text(q, "question") ?? id, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        if (q.TryGetProperty("isSecret", out var secret) && secret.ValueKind == JsonValueKind.True)
        {
            var password = new PasswordBox { Margin = new(0, 0, 0, 18), Padding = new(10), Background = (Brush)FindResource("Surface"), Foreground = (Brush)FindResource("Ink") }; content.Children.Add(password); _questions.Add((id, () => password.Password)); return;
        }
        Func<string> selected = () => "";
        if (q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            if (_request.Method == "claude/question" && q.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True)
            {
                var choices = new ListBox { SelectionMode = SelectionMode.Multiple, Margin = new(0, 0, 0, 8), MaxHeight = 180 };
                foreach (var option in options.EnumerateArray()) choices.Items.Add(CodexSession.Text(option, "label") ?? "");
                content.Children.Add(choices); selected = () => string.Join(", ", choices.SelectedItems.Cast<string>());
            }
            else
            {
                var choices = new ComboBox { Margin = new(0, 0, 0, 8) };
                foreach (var option in options.EnumerateArray()) choices.Items.Add(CodexSession.Text(option, "label") ?? "");
                content.Children.Add(choices); selected = () => choices.SelectedItem?.ToString() ?? "";
            }
            foreach (var option in options.EnumerateArray())
                if (CodexSession.Text(option, "description") is { Length: > 0 } description)
                    content.Children.Add(new TextBlock { Text = (CodexSession.Text(option, "label") ?? "") + ": " + description, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new(0,0,0,6) });
        }
        var input = new TextBox { Margin = new(0, 0, 0, 18), ToolTip = "Digite sua resposta ou selecione uma opção acima." }; content.Children.Add(input);
        _questions.Add((id, () => string.IsNullOrWhiteSpace(input.Text) ? selected() : input.Text));
    }
    private void AddFormField(Panel content, string id, JsonElement property, bool required)
    {
        content.Children.Add(new TextBlock { Text = (CodexSession.Text(property, "title") ?? id) + (required ? " *" : ""), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 7) });
        var type = CodexSession.Text(property, "type");
        if (property.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            var choices = new ComboBox { Margin = new(0, 0, 0, 8) };
            foreach (var value in options.EnumerateArray()) choices.Items.Add(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
            content.Children.Add(choices); _form.Add((id, () => choices.SelectedItem, required)); return;
        }
        if (type == "boolean")
        {
            var checkbox = new CheckBox { Content = "Sim" }; content.Children.Add(checkbox); _form.Add((id, () => checkbox.IsChecked == true, required)); return;
        }
        var input = new TextBox { Margin = new(0, 0, 0, 8) }; content.Children.Add(input);
        _form.Add((id, () => !required && string.IsNullOrWhiteSpace(input.Text) ? null : type is "integer" ? long.Parse(input.Text, System.Globalization.CultureInfo.InvariantCulture)
            : type is "number" ? double.Parse(input.Text, System.Globalization.CultureInfo.InvariantCulture)
            : type is "array" or "object" ? JsonSerializer.Deserialize<JsonElement>(input.Text) : input.Text, required));
    }
    private void Finish(bool accept)
    {
        try
        {
            Result = _request.Method switch
            {
                "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" => new { decision = accept ? "accept" : "decline" },
                "item/permissions/requestApproval" => new { permissions = accept && _request.Parameters.TryGetProperty("permissions", out var permissions) ? permissions : JsonSerializer.SerializeToElement(new { }), scope = "turn" },
                "item/tool/requestUserInput" => UserAnswers(accept),
                "claude/question" => ClaudeAnswers(accept),
                "claude/permission" => accept ? new { behavior = "allow", updatedInput = _request.Parameters.GetProperty("input") } : (object)new { behavior = "deny", message = "O usuário recusou esta solicitação. Procure uma alternativa segura." },
                "mcpServer/elicitation/request" => new { action = accept ? "accept" : "decline", content = accept ? FormAnswers() : null },
                _ => null
            };
            DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException or OverflowException)
        { _error.Text = ex.Message; }
    }
    private object UserAnswers(bool accept)
    {
        var answers = new Dictionary<string, object>();
        foreach (var (id, value) in _questions)
        {
            var text = accept ? value() : "O usuário recusou esta solicitação. Procure uma alternativa mais segura ou aguarde.";
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Responda todas as perguntas antes de enviar.");
            answers[id] = new { answers = new[] { text } };
        }
        return new { answers };
    }
    private object ClaudeAnswers(bool accept)
    {
        if (!accept) return new { behavior = "deny", message = "O usuário recusou esta solicitação." };
        var answers = new Dictionary<string, string>();
        foreach (var (id, value) in _questions)
        {
            var text = value();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Responda todas as perguntas antes de enviar.");
            answers[id] = text;
        }
        if (answers.Count == 0) throw new InvalidOperationException("Nenhuma pergunta suportada; deixe pendente ou recuse.");
        return new { answers };
    }
    private object? FormAnswers()
    {
        if (CodexSession.Text(_request.Parameters, "mode") == "url") return null;
        if (_form.Count == 0) throw new InvalidOperationException("O formulário não tem campos suportados. Deixe pendente ou recuse.");
        var content = new Dictionary<string, object?>();
        foreach (var (id, value, required) in _form)
        {
            var field = value();
            if (required && (field is null || field is string s && string.IsNullOrWhiteSpace(s))) throw new InvalidOperationException("Preencha o campo " + id + ".");
            if (field is not null) content[id] = field;
        }
        return content;
    }
}
