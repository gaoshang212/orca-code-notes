using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;
using OrcaNotes.Resources;

namespace OrcaNotes
{
    [Guid("41367a20-e93f-42af-9222-77dc11c1772c")]
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 3)]
    public sealed class OrcaNotesPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var commands = (IMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            var dte = (DTE)await GetServiceAsync(typeof(SDTE));
            var textManager = (IVsTextManager)await GetServiceAsync(typeof(SVsTextManager));
            var solution = (IVsSolution)await GetServiceAsync(typeof(SVsSolution));
            if (commands == null || dte == null || textManager == null || solution == null)
                throw new InvalidOperationException(Strings.WorkspaceServicesUnavailable);
            var command = new CommandID(new Guid("ca505c10-91b2-4082-a704-2533f7e33738"), 0x0100);
            commands.AddCommand(new MenuCommand((s, e) => EditorNotes.Try(() => SendWorkspaceNotes(dte, textManager, solution)), command));
        }

        private static void SendWorkspaceNotes(DTE dte, IVsTextManager textManager, IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string file = dte.ActiveDocument?.FullName;
            var hostKey = DefGuidList.guidIWpfTextViewHost;
            if (!string.IsNullOrEmpty(file) && ErrorHandler.Succeeded(textManager.GetActiveView(0, null, out var view)) &&
                view is IVsUserData data && ErrorHandler.Succeeded(data.GetData(ref hostKey, out var host)) &&
                host is IWpfTextViewHost wpf && wpf.TextView.Properties.TryGetProperty(typeof(EditorNotes), out EditorNotes notes) &&
                string.Equals(notes.FilePath, file, StringComparison.OrdinalIgnoreCase))
            {
                notes.OpenQueue();
                return;
            }

            string directory = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : null;
            if (!Directory.Exists(directory))
                solution.GetSolutionInfo(out directory, out _, out _);
            if (!Directory.Exists(directory))
                throw new InvalidOperationException(Strings.OpenDocumentOrSolution);

            var dialog = new SendWindow(new OrcaClient(directory), null);
            new WindowInteropHelper(dialog).Owner = dte.MainWindow.HWnd;
            dialog.ShowDialog();
        }
    }
}
