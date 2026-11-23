using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Quill.Core.AiPack.Download;
using Quill.Core.Ports;
using Quill.Windows.AiPack;
using Quill.Windows.Resources;

namespace Quill.Windows.UI.Settings;

internal enum AiPackDownloadState
{
    /// <summary>Something to download and enough space: the button is offered.</summary>
    Ready,

    /// <summary>Every component is already installed in the destination.</summary>
    NothingToDownload,

    NotEnoughSpace,
    Downloading,

    /// <summary>Cancelled by the user (or the window closed); ".partial" files are kept.</summary>
    Paused,

    Failed,
    Completed,
}

/// <summary>One progress bar of the download card.</summary>
internal sealed class AiPackComponentRow(AiPackComponent component, string name, long total) : INotifyPropertyChanged
{
    private long _bytes;
    private bool _verifying;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AiPackComponent Component { get; } = component;

    public double Percent => total == 0 ? 0 : 100.0 * _bytes / total;

    public string Text => _verifying
        ? Loc.Format("AiPackDownload_Verifying", name)
        : Loc.Format("AiPackDownload_Component", name, AiPackDownloadViewModel.FormatBytes(_bytes), AiPackDownloadViewModel.FormatBytes(total));

    public void Update(long bytes, bool verifying)
    {
        _bytes = bytes;
        _verifying = verifying;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
    }
}

/// <summary>
/// "Descargar pack de IA" card of Settings → IA. Nothing touches the network until
/// <see cref="StartAsync"/> runs from the button; <see cref="Refresh"/> only reads the disk.
/// No WPF types: the window wires buttons and raises <see cref="Ended"/> handling.
/// </summary>
internal sealed class AiPackDownloadViewModel : INotifyPropertyChanged
{
    private readonly IAiPackDownloader _downloader;
    private readonly Func<string?> _packPath;
    private AiPackDownloadOffer? _offer;
    private CancellationTokenSource? _running;
    private AiPackDownloadState _state = AiPackDownloadState.NothingToDownload;
    private string _statusText = string.Empty;
    private string _totalText = string.Empty;
    private double _totalPercent;

    public AiPackDownloadViewModel(IAiPackDownloader downloader, Func<string?> packPath)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _packPath = packPath ?? throw new ArgumentNullException(nameof(packPath));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the caller's thread after every run (completed, paused or failed), with
    /// the destination, so the window can re-check the pack and pick up a saved "ai.pack_path".</summary>
    public event EventHandler<AiPackDestination>? Ended;

    public ObservableCollection<AiPackComponentRow> Components { get; } = [];

    public AiPackDownloadState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(ShowStart));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(ShowCheckAgain));
                OnPropertyChanged(nameof(StartText));
            }
        }
    }

    public bool CanStart => State is AiPackDownloadState.Ready or AiPackDownloadState.Paused or AiPackDownloadState.Failed;

    public bool ShowStart => State is not (AiPackDownloadState.NothingToDownload or AiPackDownloadState.Downloading or AiPackDownloadState.Completed);

    public bool IsDownloading => State == AiPackDownloadState.Downloading;

    public bool ShowProgress => State is AiPackDownloadState.Downloading or AiPackDownloadState.Paused or AiPackDownloadState.Failed;

    public bool ShowCheckAgain => State == AiPackDownloadState.Completed;

    /// <summary>"Descargar pack de IA (≈3,4 GB)", "Reanudar descarga" or "Reintentar".</summary>
    public string StartText => State switch
    {
        AiPackDownloadState.Failed => Loc.Get("AiPackDownload_Retry"),
        AiPackDownloadState.Paused => Loc.Get("AiPackDownload_Resume"),
        _ when _offer?.Plan.AlreadyDownloadedBytes > 0 => Loc.Get("AiPackDownload_Resume"),
        _ => Loc.Format("AiPackDownload_Button", FormatBytes(_offer?.Plan.TotalBytes ?? 0)),
    };

    public string DestinationText => _offer is null ? string.Empty : Loc.Format("AiPackDownload_Destination", _offer.Destination.Root);

    public string FreeSpaceText => _offer switch
    {
        null => string.Empty,
        { FreeBytes: null } => Loc.Format("AiPackDownload_FreeSpaceUnknown", FormatBytes(_offer.Plan.RequiredFreeBytes)),
        _ => Loc.Format("AiPackDownload_FreeSpace", FormatBytes(_offer.FreeBytes.Value), FormatBytes(_offer.Plan.RequiredFreeBytes)),
    };

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }

    public double TotalPercent { get => _totalPercent; private set => Set(ref _totalPercent, value); }

    /// <summary>Recomputes the offer from the disk (destination, missing files, free space).</summary>
    public void Refresh()
    {
        if (State == AiPackDownloadState.Downloading)
        {
            return;
        }

        if (!TryPrepare(out var offer))
        {
            return;
        }

        StatusText = string.Empty;
        State = StateOf(offer);
        if (State == AiPackDownloadState.NothingToDownload)
        {
            StatusText = Loc.Get("AiPackDownload_NothingToDownload");
        }
        else if (State == AiPackDownloadState.NotEnoughSpace)
        {
            StatusText = NotEnoughSpaceText(offer);
        }
    }

    public async Task StartAsync(CancellationToken windowClosing)
    {
        if (!CanStart || !TryPrepare(out var offer))
        {
            return;
        }

        if (StateOf(offer) is var state && state != AiPackDownloadState.Ready)
        {
            State = state;
            StatusText = state == AiPackDownloadState.NotEnoughSpace
                ? NotEnoughSpaceText(offer)
                : Loc.Get("AiPackDownload_NothingToDownload");
            return;
        }

        Components.Clear();
        foreach (var item in offer.Plan.Items)
        {
            Components.Add(new AiPackComponentRow(item.Component, ComponentName(item.Component), item.Size));
        }

        TotalPercent = offer.Plan.TotalBytes == 0 ? 0 : 100.0 * offer.Plan.AlreadyDownloadedBytes / offer.Plan.TotalBytes;
        TotalText = Loc.Get("AiPackDownload_Starting");
        StatusText = string.Empty;
        State = AiPackDownloadState.Downloading;

        using var running = CancellationTokenSource.CreateLinkedTokenSource(windowClosing);
        _running = running;
        try
        {
            await _downloader.DownloadAsync(offer, new Progress<AiPackDownloadProgress>(OnProgress), running.Token);
            TotalPercent = 100;
            State = AiPackDownloadState.Completed;
            StatusText = Loc.Format("AiPackDownload_Done", offer.Destination.Root);
        }
        catch (OperationCanceledException)
        {
            State = AiPackDownloadState.Paused;
            StatusText = Loc.Get("AiPackDownload_Paused");
        }
        catch (AiPackDownloadException exception)
        {
            State = AiPackDownloadState.Failed;
            StatusText = FailureText(exception);
        }
        catch (HttpRequestException exception)
        {
            State = AiPackDownloadState.Failed;
            StatusText = Loc.Format("AiPackDownload_Error_Network", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            State = AiPackDownloadState.Failed;
            StatusText = Loc.Format("AiPackDownload_Error_Disk", offer.Destination.Root, exception.Message);
        }
        finally
        {
            _running = null;
        }

        Ended?.Invoke(this, offer.Destination);
    }

    public void Cancel() => _running?.Cancel();

    public static string FormatBytes(long bytes)
    {
        var culture = Loc.Culture ?? CultureInfo.CurrentUICulture;
        return bytes switch
        {
            >= 1_000_000_000 => (bytes / 1e9).ToString("0.0", culture) + " GB",
            >= 1_000_000 => (bytes / 1e6).ToString("0", culture) + " MB",
            _ => Math.Ceiling(bytes / 1e3).ToString("0", culture) + " KB",
        };
    }

    public static string FailureText(AiPackDownloadException exception)
    {
        var name = exception.Component is { } component ? ComponentName(component) : string.Empty;
        return exception.Failure switch
        {
            AiPackDownloadFailure.HashMismatch => Loc.Format("AiPackDownload_Error_HashMismatch", name),
            AiPackDownloadFailure.SizeMismatch => Loc.Format("AiPackDownload_Error_SizeMismatch", name),
            AiPackDownloadFailure.HostNotAllowed or AiPackDownloadFailure.InsecureUrl or AiPackDownloadFailure.TooManyRedirects =>
                Loc.Get("AiPackDownload_Error_HostNotAllowed"),
            AiPackDownloadFailure.HttpStatus => Loc.Format("AiPackDownload_Error_Http", exception.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "?"),
            AiPackDownloadFailure.Stalled or AiPackDownloadFailure.Incomplete => Loc.Get("AiPackDownload_Error_Interrupted"),
            _ => exception.Message,
        };
    }

    private static string ComponentName(AiPackComponent component) => Loc.Get("Settings_Ai_Component_" + component);

    private static AiPackDownloadState StateOf(AiPackDownloadOffer offer) =>
        offer.Plan.IsEmpty ? AiPackDownloadState.NothingToDownload
        : !offer.HasEnoughSpace ? AiPackDownloadState.NotEnoughSpace
        : AiPackDownloadState.Ready;

    private static string NotEnoughSpaceText(AiPackDownloadOffer offer) =>
        Loc.Format("AiPackDownload_NotEnoughSpace", offer.Destination.Root, FormatBytes(offer.Plan.RequiredFreeBytes));

    private bool TryPrepare(out AiPackDownloadOffer offer)
    {
        try
        {
            offer = _downloader.Prepare(_packPath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            offer = null!;
            State = AiPackDownloadState.Failed;
            StatusText = Loc.Format("AiPackDownload_Error_Disk", _packPath() ?? string.Empty, exception.Message);
            return false;
        }

        _offer = offer;
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(FreeSpaceText));
        OnPropertyChanged(nameof(StartText));
        return true;
    }

    private void OnProgress(AiPackDownloadProgress progress)
    {
        if (Components.FirstOrDefault(row => row.Component == progress.Component) is { } row)
        {
            row.Update(progress.ComponentBytes, progress.Phase == AiPackDownloadPhase.Verifying);
        }

        TotalPercent = progress.TotalBytes == 0 ? 0 : 100.0 * progress.TotalDone / progress.TotalBytes;
        TotalText = Loc.Format(
            "AiPackDownload_Total",
            FormatBytes(progress.TotalDone),
            FormatBytes(progress.TotalBytes),
            FormatBytes((long)progress.BytesPerSecond));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
