using Microsoft.Win32;

namespace Quill.Windows.Infrastructure;

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Quill";

    public static int Configure(bool uninstall)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("no se pudo abrir la configuracion de inicio de Windows");

        if (uninstall)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Console.WriteLine("inicio automatico desactivado");
            return 0;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("no se pudo determinar la ruta del ejecutable");
        key.SetValue(ValueName, $"\"{executable}\" run");
        Console.WriteLine("inicio automatico activado");
        return 0;
    }
}
