using Quill.Windows.Infrastructure;
using Quill.Windows.UI;

namespace Quill.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var command = CommandLine.Parse(args);
            return command.Name switch
            {
                "run" => TrayApplication.Run(ConfigStore.ResolveRecordingsRoot(command.OutputDirectory)),
                "doctor" => Doctor.Run(ConfigStore.ResolveRecordingsRoot(command.OutputDirectory)),
                "install" => AutoStart.Configure(command.Uninstall),
                "help" => CommandLine.PrintHelp(),
                _ => CommandLine.PrintHelp(),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"quill: {exception.Message}");
            return 1;
        }
    }
}

internal sealed record Command(string Name, string? OutputDirectory, bool Uninstall);

internal static class CommandLine
{
    public static Command Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new Command("run", null, false);
        }

        var name = args[0].ToLowerInvariant();
        if (name is "--help" or "-h")
        {
            return new Command("help", null, false);
        }

        string? outputDirectory = null;
        var uninstall = false;
        var launchAtLogin = false;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--out" when index + 1 < args.Length:
                    outputDirectory = args[++index];
                    break;
                case "--launch-at-login":
                    launchAtLogin = true;
                    break;
                case "--uninstall":
                    uninstall = true;
                    break;
                default:
                    throw new ArgumentException($"unknown option: {args[index]}");
            }
        }

        if (name is not ("run" or "doctor" or "install"))
        {
            throw new ArgumentException($"unknown command: {name}");
        }

        if (name == "install")
        {
            if (outputDirectory is not null)
            {
                throw new ArgumentException("--out solo se puede usar con run o doctor");
            }

            if (launchAtLogin == uninstall)
            {
                throw new ArgumentException("usa --launch-at-login o --uninstall, pero no ambos");
            }
        }
        else if (launchAtLogin || uninstall)
        {
            throw new ArgumentException("esta opcion solo se puede usar con install");
        }

        return new Command(name, outputDirectory, uninstall);
    }

    public static int PrintHelp()
    {
        Console.WriteLine("""
            quill - grabador y transcriptor local para Windows 11

            quill.exe                         Ejecuta la aplicacion de bandeja.
            quill.exe run --out <directorio>   Cambia el directorio de sesiones.
            quill.exe doctor                   Comprueba audio, modelo y carpeta.
            quill.exe install --launch-at-login
            quill.exe install --uninstall
            """);
        return 0;
    }
}
