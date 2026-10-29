using Microsoft.Win32;

internal static class InstallationState
{
    internal static string? Read(bool includeApi=false)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows installation state requires Windows.");
        using var root=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
        string id=includeApi ? "{D5C0CE54-F622-40E2-9F4A-D70D17C31BD2}_is1" : "{604952AE-033E-4832-9071-A9F995BA8C1D}_is1";
        using var key=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\"+id);
        if(key==null)return null;
        if(key.GetValue("InstallLocation") is not string location || !Path.IsPathFullyQualified(location))
            throw new InvalidDataException("Existing installation location is unknown; refusing artifact QA.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
    }
}
