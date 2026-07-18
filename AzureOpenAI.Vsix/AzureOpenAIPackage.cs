using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace AzureOpenAI.Vsix;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Azure OpenAI Chat", "Call Azure OpenAI from Visual Studio and load config.toml", "1.6")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(ChatToolWindow))]
[Guid(PackageGuidString)]
public sealed class AzureOpenAIPackage : AsyncPackage
{
    public const string PackageGuidString = "9a0df5cd-71f3-4b74-a8c1-9df90f5a483a";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await OpenChatToolWindowCommand.InitializeAsync(this);
    }
}










