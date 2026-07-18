using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace AzureOpenAI.Vsix;

public partial class ChatToolWindowControl : UserControl
{
    private const int MaxRenderedShellOutputChars = 16000;

    private AzureOpenAIConfig? _currentConfig;
    private bool _isRequestInFlight;
    private ChatImageAttachment? _pendingImageAttachment;

    private sealed class MessageBubbleHandle
    {
        public MessageBubbleHandle(Border border, TextBox messageBox, bool isUser)
        {
            Border = border;
            MessageBox = messageBox;
            IsUser = isUser;
        }

        public Border Border { get; }
        public TextBox MessageBox { get; }
        public bool IsUser { get; }
        public DispatcherTimer? Timer { get; set; }
    }

    public ChatToolWindowControl()
    {
        InitializeComponent();
        UpdateImageAttachmentBadge();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ReloadConfigAsync();
    }

    private async void ReloadConfigButton_Click(object sender, RoutedEventArgs e)
    {
        await ReloadConfigAsync();
    }

    private async void QuestionTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TryAttachImageFromClipboard())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e)
    {
        await SendPromptAsync();
    }
    private void RemoveImageAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        ClearPendingImageAttachment();
        StatusText.Text = "状态：已移除图片附件。";
    }


    private async Task SendPromptAsync()
    {
        if (_isRequestInFlight)
        {
            return;
        }

        var prompt = (QuestionTextBox.Text ?? string.Empty).Trim();
        var imageAttachment = _pendingImageAttachment;

        if (string.IsNullOrWhiteSpace(prompt) && imageAttachment is null)
        {
            StatusText.Text = "状态：请先输入问题或粘贴图片。";
            return;
        }

        var isShellCommand = TryParseShellCommand(prompt, out var shellCommand);

        MessageBubbleHandle? thinkingBubble = null;
        try
        {
            _isRequestInFlight = true;
            AskButton.IsEnabled = false;

            var userRenderedPrompt = BuildUserDisplayText(prompt, imageAttachment);
            AppendMessage(userRenderedPrompt, isUser: true);
            QuestionTextBox.Clear();
            ClearPendingImageAttachment();

            if (isShellCommand)
            {
                StatusText.Text = "状态：正在执行 shell_command...";
                thinkingBubble = AppendThinkingBubble("正在执行 shell_command");

                var result = await ShellCommandExecutor.ExecuteAsync(shellCommand!, timeoutSeconds: 180, CancellationToken.None, sourceHint: "manual").ConfigureAwait(true);
                var rendered = FormatShellCommandResult(result);
                CompleteThinkingBubble(thinkingBubble, rendered, isError: result.ExitCode != 0 || result.TimedOut);

                StatusText.Text = result.ExitCode == 0 && !result.TimedOut
                    ? "状态：shell_command 执行完成。"
                    : "状态：shell_command 执行结束（存在错误或超时）。";

                return;
            }

            StatusText.Text = "状态：正在发送请求...";
            thinkingBubble = AppendThinkingBubble();

            if (_currentConfig is null)
            {
                await ReloadConfigAsync();
                if (_currentConfig is null)
                {
                    CompleteThinkingBubble(thinkingBubble, "配置未加载，无法请求。", isError: true);
                    StatusText.Text = "状态：配置未加载，无法请求。";
                    return;
                }
            }

            string answer = await AzureOpenAIChatClient.AskAsync(_currentConfig, prompt, imageAttachment, CancellationToken.None);
            CompleteThinkingBubble(thinkingBubble, answer, isError: false);
            StatusText.Text = "状态：请求完成。";

            if (TryExtractShellCommandFromAssistantAnswer(answer, out var aiCommand, out var source))
            {
                var autoExecBubble = AppendThinkingBubble("正在执行 AI 返回命令");
                var execResult = await ShellCommandExecutor.ExecuteAsync(aiCommand!, timeoutSeconds: 180, CancellationToken.None, sourceHint: source).ConfigureAwait(true);

                var summary = new StringBuilder();
                summary.AppendLine("检测到 AI 返回可执行命令并已自动执行。");
                summary.AppendLine($"来源: {source}");
                summary.AppendLine("命令:");
                summary.AppendLine(aiCommand);
                summary.AppendLine();
                summary.Append(FormatShellCommandResult(execResult));

                CompleteThinkingBubble(autoExecBubble, summary.ToString(), isError: execResult.ExitCode != 0 || execResult.TimedOut);
                StatusText.Text = execResult.ExitCode == 0 && !execResult.TimedOut
                    ? "状态：AI 命令执行完成。"
                    : "状态：AI 命令执行结束（存在错误或超时）。";
            }
        }
        catch (AzureOpenAIChatClient.ImageInputNotSupportedException ex)
        {
            var friendlyMessage = ex.Message;

            if (thinkingBubble is not null)
            {
                CompleteThinkingBubble(thinkingBubble, friendlyMessage, isError: true);
            }
            else
            {
                AppendMessage(friendlyMessage, isUser: false, isError: true);
            }

            StatusText.Text = "状态：当前模型不支持图片输入。";
        }
        catch (Exception ex)
        {
            if (thinkingBubble is not null)
            {
                CompleteThinkingBubble(thinkingBubble, ex.ToString(), isError: true);
            }
            else
            {
                AppendMessage(ex.ToString(), isUser: false, isError: true);
            }

            StatusText.Text = "状态：请求失败。";
        }
        finally
        {
            _isRequestInFlight = false;
            AskButton.IsEnabled = true;
        }
    }

        private bool TryAttachImageFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return false;
            }

            var bitmapSource = Clipboard.GetImage();
            if (bitmapSource is null)
            {
                return false;
            }

            var pngBytes = EncodeBitmapToPng(bitmapSource);
            if (pngBytes.Length == 0)
            {
                return false;
            }

            _pendingImageAttachment = ChatImageAttachment.FromPngBytes(
                pngBytes,
                width: bitmapSource.PixelWidth,
                height: bitmapSource.PixelHeight);

            UpdateImageAttachmentBadge();
            StatusText.Text = "状态：已粘贴图片，可直接发送。";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：粘贴图片失败。";
            AppendMessage(ex.ToString(), isUser: false, isError: true);
            return false;
        }
    }

    private static byte[] EncodeBitmapToPng(BitmapSource bitmapSource)
    {
        using (var memoryStream = new MemoryStream())
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(memoryStream);
            return memoryStream.ToArray();
        }
    }

    private void ClearPendingImageAttachment()
    {
        _pendingImageAttachment = null;
        UpdateImageAttachmentBadge();
    }

    private void UpdateImageAttachmentBadge()
    {
        if (_pendingImageAttachment is null)
        {
            ImageAttachmentBadge.Visibility = Visibility.Collapsed;
            ImageAttachmentText.Text = "🖼 已附加图片";
            return;
        }

        var sizeKb = Math.Max(1, _pendingImageAttachment.ContentBytes.Length / 1024);
        ImageAttachmentText.Text = $"🖼 已附加图片 {_pendingImageAttachment.Width}×{_pendingImageAttachment.Height} ({sizeKb} KB)";
        ImageAttachmentBadge.Visibility = Visibility.Visible;
    }

    private static string BuildUserDisplayText(string prompt, ChatImageAttachment? imageAttachment)
    {
        if (imageAttachment is null)
        {
            return prompt;
        }

        const string attachmentTag = "[已附加图片]";
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return attachmentTag;
        }

        return prompt + Environment.NewLine + attachmentTag;
    }
private async Task ReloadConfigAsync()
    {
        try
        {
            StatusText.Text = "状态：正在加载当前用户 .codex/config.toml...";
            var loaded = await ConfigTomlLoader.LoadAsync(solutionDirectory: null, cancellationToken: CancellationToken.None);

            _currentConfig = loaded.Config;
            ConfigPathText.Text = $"Config: {loaded.LoadedPath}";
            StatusText.Text = $"状态：配置加载成功（API={_currentConfig.WireApi}, Deployment={_currentConfig.Deployment}）。提示：输入 /shell <命令> 可走 shell_command 通道。";
        }
        catch (Exception ex)
        {
            _currentConfig = null;
            ConfigPathText.Text = "Config: (加载失败)";
            StatusText.Text = $"状态：{ex.Message}";
            AppendMessage(ex.ToString(), isUser: false, isError: true);
        }
    }

    private void AppendMessage(string text, bool isUser, bool isError = false)
    {
        var bubble = CreateBubble(text, isUser, isError);
        MessagesPanel.Children.Add(bubble.Border);
        ChatScrollViewer.ScrollToEnd();
    }

    private MessageBubbleHandle AppendThinkingBubble(string baseText = "正在思考中")
    {
        var bubble = CreateBubble(baseText, isUser: false, isError: false);
        MessagesPanel.Children.Add(bubble.Border);

        var frames = new[]
        {
            baseText,
            baseText + ".",
            baseText + "..",
            baseText + "..."
        };

        var frameIndex = 0;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };

        timer.Tick += (_, _) =>
        {
            frameIndex = (frameIndex + 1) % frames.Length;
            bubble.MessageBox.Text = frames[frameIndex];
            ChatScrollViewer.ScrollToEnd();
        };

        bubble.Timer = timer;
        timer.Start();
        ChatScrollViewer.ScrollToEnd();

        return bubble;
    }

    private void CompleteThinkingBubble(MessageBubbleHandle bubble, string finalText, bool isError)
    {
        bubble.Timer?.Stop();
        bubble.Timer = null;

        bubble.MessageBox.Text = finalText;
        ApplyBubbleVisual(bubble.Border, bubble.IsUser, isError);

        ChatScrollViewer.ScrollToEnd();
    }

    private MessageBubbleHandle CreateBubble(string text, bool isUser, bool isError)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = 680,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BorderThickness = isUser ? new Thickness(1.5) : new Thickness(1)
        };

        ApplyBubbleVisual(border, isUser, isError);

        var messageBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        messageBox.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        messageBox.SetResourceReference(TextElement.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
        messageBox.SetResourceReference(TextElement.FontSizeProperty, VsFonts.EnvironmentFontSizeKey);

        border.Child = messageBox;
        return new MessageBubbleHandle(border, messageBox, isUser);
    }

    private void ApplyBubbleVisual(Border border, bool isUser, bool isError)
    {
        if (isError)
        {
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(190, 70, 70));
            border.Background = new SolidColorBrush(Color.FromArgb(25, 190, 70, 70));
            return;
        }

        border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        border.Opacity = 1.0;
    }

    private static bool TryParseShellCommand(string prompt, out string? command)
    {
        command = null;

        var text = (prompt ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        const string prefix1 = "/shell";
        const string prefix2 = "/sh";
        const string prefix3 = "shell_command:";

        if (text.StartsWith(prefix1 + " ", StringComparison.OrdinalIgnoreCase))
        {
            command = text.Substring(prefix1.Length).Trim();
            return command.Length > 0;
        }

        if (text.StartsWith(prefix2 + " ", StringComparison.OrdinalIgnoreCase))
        {
            command = text.Substring(prefix2.Length).Trim();
            return command.Length > 0;
        }

        if (text.StartsWith(prefix3, StringComparison.OrdinalIgnoreCase))
        {
            command = text.Substring(prefix3.Length).Trim();
            return command.Length > 0;
        }

        return false;
    }

    private static bool TryExtractShellCommandFromAssistantAnswer(string answer, out string? command, out string source)
    {
        command = null;
        source = string.Empty;

        var text = (answer ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        const string marker = "shell_command:";
        if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            var full = text.Substring(marker.Length).Trim();
            if (!string.IsNullOrWhiteSpace(full))
            {
                command = full;
                source = "shell_command";
                return true;
            }
        }

        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var singleLine = line.Substring(marker.Length).Trim();
            if (!string.IsNullOrWhiteSpace(singleLine))
            {
                command = singleLine;
                source = "shell_command(line)";
                return true;
            }
        }

        string? bestFenceCommand = null;
        string bestFenceSource = string.Empty;
        var bestFencePriority = int.MinValue;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var language = line.Substring(3).Trim();
            if (!IsShellFenceLanguage(language))
            {
                continue;
            }

            var normalizedLanguage = NormalizeFenceLanguage(language);
            var priority = GetFencePriority(normalizedLanguage);

            var sb = new StringBuilder();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var contentLine = lines[j];
                if (contentLine.Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    var fenced = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(fenced) && priority > bestFencePriority)
                    {
                        bestFencePriority = priority;
                        bestFenceCommand = fenced;
                        bestFenceSource = "fence:" + normalizedLanguage;
                    }

                    break;
                }

                sb.AppendLine(contentLine);
            }
        }

        if (!string.IsNullOrWhiteSpace(bestFenceCommand))
        {
            command = bestFenceCommand;
            source = bestFenceSource;
            return true;
        }

        var startTagIndex = text.IndexOf("<shell_command>", StringComparison.OrdinalIgnoreCase);
        if (startTagIndex >= 0)
        {
            var contentStart = startTagIndex + "<shell_command>".Length;
            var endTagIndex = text.IndexOf("</shell_command>", contentStart, StringComparison.OrdinalIgnoreCase);
            if (endTagIndex > contentStart)
            {
                var payload = text.Substring(contentStart, endTagIndex - contentStart).Trim();
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    command = payload;
                    source = "xml-tag:shell_command";
                    return true;
                }
            }
        }

        return false;
    }

    private static int GetFencePriority(string normalizedLanguage)
    {
        switch (normalizedLanguage)
        {
            case "powershell":
            case "pwsh":
            case "ps":
            case "ps1":
                return 300;
            case "cmd":
            case "bat":
            case "batch":
                return 200;
            case "shell":
            case "sh":
                return 100;
            default:
                return 0;
        }
    }
    private static bool IsShellFenceLanguage(string rawLanguage)
    {
        var language = NormalizeFenceLanguage(rawLanguage);
        switch (language)
        {
            case "powershell":
            case "pwsh":
            case "ps":
            case "ps1":
            case "shell":
            case "sh":
            case "cmd":
            case "bat":
            case "batch":
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeFenceLanguage(string rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage))
        {
            return string.Empty;
        }

        var token = rawLanguage.Trim();
        var split = token.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return split.Length == 0 ? string.Empty : split[0].Trim().ToLowerInvariant();
    }

    private static string FormatShellCommandResult(ShellCommandResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("shell_command 执行结果");
        sb.AppendLine($"ExitCode: {result.ExitCode}");
        sb.AppendLine($"Duration: {result.Duration.TotalMilliseconds:F0} ms");
        if (result.TimedOut)
        {
            sb.AppendLine("TimedOut: true");
        }

        sb.AppendLine();
        sb.AppendLine("STDOUT:");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.StdOut)
            ? "(empty)"
            : TruncateForUi(result.StdOut));

        sb.AppendLine();
        sb.AppendLine("STDERR:");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.StdErr)
            ? "(empty)"
            : TruncateForUi(result.StdErr));

        return sb.ToString().TrimEnd();
    }

    private static string TruncateForUi(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxRenderedShellOutputChars)
        {
            return text;
        }

        return text.Substring(0, MaxRenderedShellOutputChars)
             + Environment.NewLine
             + Environment.NewLine
             + $"...（已截断，原始长度 {text.Length} 字符）";
    }
}

internal sealed class ShellCommandResult
{
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = string.Empty;
    public string StdErr { get; set; } = string.Empty;
    public bool TimedOut { get; set; }
    public TimeSpan Duration { get; set; }
}

internal static class ShellCommandExecutor
{
    private enum CommandShellKind
    {
        PowerShell,
        Cmd
    }

    public static async Task<ShellCommandResult> ExecuteAsync(string command, int timeoutSeconds, CancellationToken cancellationToken, string? sourceHint = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("shell_command 为空。请在 /shell 后面输入实际命令。");
        }

        return await Task.Run(() => Execute(command, timeoutSeconds, cancellationToken, sourceHint), cancellationToken).ConfigureAwait(false);
    }

    private static ShellCommandResult Execute(string command, int timeoutSeconds, CancellationToken cancellationToken, string? sourceHint)
    {
        if (timeoutSeconds <= 0)
        {
            timeoutSeconds = 120;
        }

        var start = DateTimeOffset.UtcNow;
        var shellKind = ResolveShellKind(command, sourceHint);

        var psi = BuildProcessStartInfo(command, shellKind);

        using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 shell_command 进程。请确认 PowerShell/CMD 可用。");
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            var timeoutMs = checked(timeoutSeconds * 1000);
            var exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                TryKillProcess(process);
            }

            process.WaitForExit(2000);
            Task.WaitAll(new Task[] { stdOutTask, stdErrTask }, 5000);

            cancellationToken.ThrowIfCancellationRequested();

            var stdOut = stdOutTask.Status == TaskStatus.RanToCompletion ? stdOutTask.Result : string.Empty;
            var stdErr = stdErrTask.Status == TaskStatus.RanToCompletion ? stdErrTask.Result : string.Empty;

            var end = DateTimeOffset.UtcNow;
            return new ShellCommandResult
            {
                ExitCode = exited ? process.ExitCode : -1,
                TimedOut = !exited,
                Duration = end - start,
                StdOut = stdOut,
                StdErr = exited ? stdErr : (stdErr + Environment.NewLine + "命令执行超时，已终止。")
            };
        }
    }

    private static ProcessStartInfo BuildProcessStartInfo(string command, CommandShellKind shellKind)
    {
        if (shellKind == CommandShellKind.Cmd)
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c " + command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }

        var wrappedPowerShell = "$ProgressPreference='SilentlyContinue';" + command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedPowerShell));
        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
    }

    private static CommandShellKind ResolveShellKind(string command, string? sourceHint)
    {
        var hint = (sourceHint ?? string.Empty).Trim().ToLowerInvariant();
        if (hint.StartsWith("fence:cmd", StringComparison.Ordinal)
            || hint.StartsWith("fence:bat", StringComparison.Ordinal)
            || hint.StartsWith("fence:batch", StringComparison.Ordinal))
        {
            return CommandShellKind.Cmd;
        }

        var cmdStyleHints = new[] { "%userprofile%", "%cd%", "%temp%", "&&", "||" };
        var lowerCommand = (command ?? string.Empty).ToLowerInvariant();
        foreach (var token in cmdStyleHints)
        {
            if (lowerCommand.Contains(token))
            {
                return CommandShellKind.Cmd;
            }
        }

        return CommandShellKind.PowerShell;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }
}



