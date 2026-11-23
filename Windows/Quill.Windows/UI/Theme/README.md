# Quill design system (UI/Theme)

Fluent 2 look on top of [WPF-UI](https://github.com/lepoco/wpfui) 4.3. Everything here is
installed once by `ThemeBootstrapper.Initialize(application)` (called from
`TrayApplication.Run`), so every window gets it without merging dictionaries itself.

## What is installed

Application resources, in merge order:

1. WPF-UI `ThemesDictionary` (light, dark or high contrast, from the Windows app mode)
2. WPF-UI `ControlsDictionary` (implicit Fluent styles for standard controls)
3. `Tokens.xaml`: spacing, radii, typography, elevation, motion
4. `Colors.Light.xaml` or `Colors.Dark.xaml`: Quill semantic colors, swapped live when Windows changes theme
5. `Controls/Controls.xaml`: default styles for the Quill controls below

Rules:

- Use `DynamicResource` for every color/brush so theme changes apply live. Tokens that never change (spacing, radii, fonts, sizes) can use `StaticResource`.
- Neutral surfaces, text and accent come from WPF-UI: `ApplicationBackgroundBrush`, `CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, `TextFillColorPrimaryBrush` / `Secondary` / `Tertiary`, `AccentTextFillColorPrimaryBrush`, `DividerStrokeColorDefaultBrush`.
- Never hard-code a color in a view. If a role is missing, add it to both `Colors.*.xaml` files with the same key.
- All UI copy goes through `Resources/Strings.resx` (see Strings below).
- WPF-UI's root namespace is `Wpf`: never declare `using Wpf = ...` in this project.

## New windows (F6 main window, F7 companion panel, F9 settings)

```xml
<ui:FluentWindow x:Class="Quill.Windows.UI.MainWindow"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:qc="clr-namespace:Quill.Windows.UI.Theme.Controls"
    xmlns:loc="clr-namespace:Quill.Windows.Resources"
    ExtendsContentIntoTitleBar="True"
    WindowBackdropType="Mica">
    <Grid>
        <ui:TitleBar Title="Quill" />
        ...
    </Grid>
</ui:FluentWindow>
```

```csharp
public MainWindow()
{
    InitializeComponent();
    ThemeBootstrapper.Attach(this); // Mica on Windows 11, solid background on Windows 10 / high contrast
}
```

## Tokens (Tokens.xaml)

| Group | Keys |
|---|---|
| Spacing (double) | `Quill.Space.XXS` 2, `XS` 4, `S` 8, `M` 12, `L` 16, `XL` 24, `XXL` 32, `XXXL` 48 |
| Padding/margin (Thickness) | `Quill.Padding.Window`, `.Card`, `.CardCompact`, `.Pill`, `.Chip`; `Quill.Margin.Stack`, `.Section`, `.SectionHeader` |
| Radii (CornerRadius) | `Quill.Radius.Small` 4, `.Medium` 8, `.Large` 12, `.Pill` |
| Fonts | `Quill.Font.Text` (Segoe UI Variable Text), `.Display`, `.Mono`; sizes `Quill.FontSize.Caption` 12, `.Body` 14, `.BodyLarge` 18, `.Subtitle` 20, `.Title` 28, `.TitleLarge` 40 |
| Text styles (TextBlock) | `Quill.Text.Caption`, `.Body`, `.BodyStrong`, `.Secondary`, `.BodyLarge`, `.Subtitle`, `.Title`, `.TitleLarge`, `.Mono` |
| Elevation (Effect) | `Quill.Elevation.Card`, `.Raised`, `.Flyout` |
| Motion (Duration) | `Quill.Motion.Fast`, `.Normal`, `.Slow` |

```xml
<TextBlock Style="{StaticResource Quill.Text.Title}" Text="{loc:Str Summary_ExecutiveSummary}" />
<Border Effect="{StaticResource Quill.Elevation.Flyout}" CornerRadius="{StaticResource Quill.Radius.Large}" />
```

## Semantic colors (Colors.Light.xaml / Colors.Dark.xaml)

Each role has `Quill.Color.<Role>` / `Quill.Brush.<Role>` (strong: text, dots, icons) and
`<Role>Subtle` (tinted background):
`Recording`, `Me`, `Success`, `Warning`, `Info`, `Ai`, `Neutral`.
Also `Quill.Brush.Speaker1`..`Speaker8` (palette for "Speaker N"; blue and red are reserved
for Me and Recording) and `Quill.Brush.MeterTrack` / `MeterLow` / `MeterHigh`.

```xml
<Ellipse Fill="{DynamicResource Quill.Brush.Recording}" Width="8" Height="8" />
```

## Controls (namespace `Quill.Windows.UI.Theme.Controls`, `xmlns:qc`)

```xml
<!-- Card: rounded surface; IsInteractive adds hover fill + hand cursor -->
<qc:Card>
    <StackPanel>
        <!-- SectionHeader: title, optional count badge, optional right action -->
        <qc:SectionHeader Title="{loc:Str Summary_Decisions}" Count="{Binding Decisions.Count}">
            <qc:SectionHeader.Action>
                <ui:Button Content="{loc:Str Action_Copy}" Appearance="Transparent" />
            </qc:SectionHeader.Action>
        </qc:SectionHeader>
        <ItemsControl ItemsSource="{Binding Decisions}" />
        <!-- EmptyState: "Ninguno" by default; Description adds a second line -->
        <qc:EmptyState Visibility="{Binding HasNoDecisions, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>
</qc:Card>

<!-- SpeakerChip: color is stable per id ("S3" -> slot 3, "me" -> Me color);
     label defaults to "Yo" / "Interlocutor N" / the id; Label overrides it (renamed speaker) -->
<qc:SpeakerChip SpeakerId="{Binding SpeakerId}" Label="{Binding SpeakerName}" />

<!-- StatusPill: Recording | Paused | Transcribing | Summarizing; Text overrides the label -->
<qc:StatusPill Status="Recording" Text="{Binding ElapsedLabel}" />

<!-- TimestampLink: a button showing "mm:ss" or "h:mm:ss"; Time also accepts "hh:mm:ss" strings -->
<qc:TimestampLink Time="{Binding At}" Command="{Binding SeekCommand}" CommandParameter="{Binding At}" />

<!-- LevelMeter: 0..1 level bar, turns red from HotThreshold (0.85); default 64x4, stretches -->
<qc:LevelMeter Level="{Binding MicLevel}" Width="80" />
```

Helpers usable from view models and tests: `SpeakerChip.PaletteSlot(id)`,
`SpeakerChip.TrailingNumber(id)`, `TimestampLink.Format(TimeSpan)`, `StatusPill.LabelKey(status)`,
`LevelMeter.ClampUnit(value)`.

## Strings (Resources/Strings.resx)

Spanish is the neutral language. Keys are `Area_Name`.

```xml
xmlns:loc="clr-namespace:Quill.Windows.Resources"
<TextBlock Text="{loc:Str Summary_OpenQuestions}" />
```

```csharp
var title = Loc.Get("Summary_Risks");                 // "Riesgos y bloqueos"
var speaker = Loc.Format("Speaker_Numbered", 2);       // "Interlocutor 2"
```

A missing key renders as `[Key]` so typos are visible. Seeded areas: `Summary_*` (all
MeetingSummary sections, `Summary_MyTasks`), `Common_None`, `Status_*`, `Action_*`,
`Speaker_*`, `A11y_*`.
