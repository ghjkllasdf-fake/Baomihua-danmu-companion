using Microsoft.Win32;

internal static class InstallationState
{
    internal static string? Read()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows installation state requires Windows.");
        using var root=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
        using var key=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{604952AE-033E-4832-9071-A9F995BA8C1D}_is1");
        if(key==null)return null;
        if(key.GetValue("InstallLocation") is not string location || !Path.IsPathFullyQualified(location))
            throw new InvalidDataException("Existing installation location is unknown; refusing artifact QA.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
    }
}
