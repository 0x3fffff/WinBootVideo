namespace WinBootVideo;

internal static class AppVersion
{
    internal static string Display => typeof(AppVersion).Assembly.GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}" : "未知版本";
}
