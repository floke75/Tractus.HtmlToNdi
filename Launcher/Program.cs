using System;
using System.Windows.Forms;

namespace Tractus.HtmlToNdi.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LauncherForm());
    }
}
