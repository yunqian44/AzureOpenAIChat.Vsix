using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AzureOpenAI.Vsix;

public partial class ChatToolWindowControl : UserControl
{
    private AzureOpenAIConfig? _currentConfig;

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
        var prompt = (QuestionTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "状态：请先输入问题。";
            return;
        }

        try
        {
            AskButton.IsEnabled = false;

            AppendMessage(prompt, isUser: true);
            QuestionTextBox.Clear();
            StatusText.Text = "状态：正在发送请求...";

            if (_currentConfig is null)
            {
                await ReloadConfigAsync();
                if (_currentConfig is null)
                {
                    StatusText.Text = "状态：配置未加载，无法请求。";
                    return;
                }
            }

            string answer = await AzureOpenAIChatClient.AskAsync(_currentConfig, prompt, CancellationToken.None);
            AppendMessage(answer, isUser: false);
            StatusText.Text = "状态：请求完成。";
        }
        catch (Exception ex)
        {
            AppendMessage(ex.ToString(), isUser: false, isError: true);
            StatusText.Text = "状态：请求失败。";
        }
        finally
        {
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
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = 680,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            BorderBrush = isError
                ? new SolidColorBrush(Color.FromRgb(190, 70, 70))
                : new SolidColorBrush(Color.FromArgb(90, 127, 127, 127)),
            Background = isUser
                ? new SolidColorBrush(Color.FromArgb(36, 0, 122, 204))
                : new SolidColorBrush(Color.FromArgb(20, 127, 127, 127))
        };

        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };

        border.Child = textBlock;
        MessagesPanel.Children.Add(border);

        ChatScrollViewer.ScrollToEnd();
    }
}

