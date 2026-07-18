using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AzureOpenAI.Vsix;

internal sealed class OpenChatToolWindowCommand
{
    public const int CommandId = 0x0100;
    public static readonly Guid CommandSet = new Guid("3d379e9f-c233-46bd-92fd-b8efecf165f3");

    private readonly AsyncPackage _package;

    private OpenChatToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;

        var menuCommandId = new CommandID(CommandSet, CommandId);
        var menuItem = new MenuCommand(Execute, menuCommandId);
        commandService.AddCommand(menuItem);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService == null)
        {
            throw new InvalidOperationException("Cannot get IMenuCommandService.");
        }

        _ = new OpenChatToolWindowCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        _ = _package.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                ToolWindowPane window = await _package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, true, _package.DisposalToken);
                if (window?.Frame == null)
                {
                    throw new InvalidOperationException("Cannot create Azure OpenAI Chat tool window.");
                }
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                VsShellUtilities.ShowMessageBox(
                    _package,
                    "打开 Azure OpenAI Chat 窗口失败：\n" + ex.Message,
                    "Azure OpenAI Chat",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        });
    }
}
