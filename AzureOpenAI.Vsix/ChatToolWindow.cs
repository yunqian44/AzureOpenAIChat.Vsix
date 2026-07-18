using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace AzureOpenAI.Vsix;

[Guid("d31bca97-0a69-44d1-8bf4-292fbcfce5c6")]
public sealed class ChatToolWindow : ToolWindowPane
{
    public ChatToolWindow() : base(null)
    {
        Caption = "Azure OpenAI Chat";
        Content = new ChatToolWindowControl();
    }
}
