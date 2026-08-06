using Microsoft.Win32;

namespace Quill.Windows.Infrastructure;

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Quill";

    public static int Configure(bool uninstall)
    {
        if (uninstall)
        {
            using var existingKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            Console.WriteLine("inicio automatico desactivado");
            return 0;
        }

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("no se pudo abrir la configuracion de inicio de Windows");

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("no se pudo determinar la ruta del ejecutable");
        runKey.SetValue(ValueName, $"\"{executable}\" run");
        Console.WriteLine("inicio automatico activado");
        return 0;
    }
}
