namespace STranslate.Core;

public enum AppShutdownReason
{
    ExternalOrUnknown,
    SystemSessionEnding,
    TrayMenu,
    TrayDoubleClick,
    KeyboardShortcut,
    ApplicationUpdate,
    LocalBackup,
    LocalRestore,
    WebDavBackup,
    WebDavRestore,
    PortableModeChange,
    PluginChange,
    S3Backup,
    S3Restore,
}
