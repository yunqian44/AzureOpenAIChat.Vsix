using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace AzureOpenAI.Vsix;

public partial class ChatToolWindowControl : UserControl
{
    private AzureOpenAIConfig? _currentConfig;
    private bool _isRequestInFlight;

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

    private async Task SendPromptAsync()
    {
        if (_isRequestInFlight)
        {
            return;
        }

        var prompt = (QuestionTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "状态：请先输入问题。";
            return;
        }

        MessageBubbleHandle? thinkingBubble = null;
        try
        {
            _isRequestInFlight = true;
            AskButton.IsEnabled = false;

            AppendMessage(prompt, isUser: true);
            QuestionTextBox.Clear();
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

            string answer = await AzureOpenAIChatClient.AskAsync(_currentConfig, prompt, CancellationToken.None);
            CompleteThinkingBubble(thinkingBubble, answer, isError: false);
            StatusText.Text = "状态：请求完成。";
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

    private async Task ReloadConfigAsync()
    {
        try
        {
            StatusText.Text = "状态：正在加载当前用户 .codex/config.toml...";
            var loaded = await ConfigTomlLoader.LoadAsync(solutionDirectory: null, cancellationToken: CancellationToken.None);

            _currentConfig = loaded.Config;
            ConfigPathText.Text = $"Config: {loaded.LoadedPath}";
            StatusText.Text = $"状态：配置加载成功（API={_currentConfig.WireApi}, Deployment={_currentConfig.Deployment}）。";
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

    private MessageBubbleHandle AppendThinkingBubble()
    {
        var bubble = CreateBubble("正在思考中", isUser: false, isError: false);
        MessagesPanel.Children.Add(bubble.Border);

        var frames = new[]
        {
            "正在思考中",
            "正在思考中.",
            "正在思考中..",
            "正在思考中..."
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
}


