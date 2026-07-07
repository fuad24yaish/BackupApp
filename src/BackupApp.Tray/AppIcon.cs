namespace BackupApp.Tray;

/// <summary>The application icon, loaded once from the icon embedded in the exe.</summary>
internal static class AppIcon
{
    private static readonly Icon _icon = Load();

    public static Icon Value => _icon;

    private static Icon Load()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null)
                    return extracted;
            }
        }
        catch
        {
            // fall through to a stock icon if the exe icon can't be read
        }
        return SystemIcons.Shield;
    }
}
