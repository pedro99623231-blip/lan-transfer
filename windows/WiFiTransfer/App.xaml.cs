using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Windows;

namespace WiFiTransfer;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "WiFiTransfer.SingleInstance", out bool created);
        if (!created)
        {
            // Já está aberto: manda os arquivos/pastas (menu do Explorer) para a janela aberta e sai.
            if (!ForwardToRunning(e.Args) && !e.Args.Contains("--minimized"))
                MessageBox.Show("O LAN Transfer já está aberto.", "LAN Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        Translator.Install(Settings.Load().Language);
        base.OnStartup(e);
    }

    private static bool ForwardToRunning(string[] args)
    {
        if (args.Contains("--minimized")) return true;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", WiFiTransfer.MainWindow.PipeName, PipeDirection.Out);
                pipe.Connect(500);
                using var w = new StreamWriter(pipe);
                w.Write(string.Join("\n", args.Length > 0 ? args : new[] { "--show" }));
                w.Flush();
                return true;
            }
            catch { Thread.Sleep(250); }
        }
        return false;
    }
}
