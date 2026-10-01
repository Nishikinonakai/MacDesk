# Windows issue regression checks

Run `dotnet run --project tests/IssueRegression/IssueRegression.csproj -c Release` on Windows with the .NET 10 SDK.

The executable measures real WPF labels across 4,500 combinations of DPI, font, weight, label size and icon size; checks expanded rename layout at screen edges and commit/cancel; and loads two distinct thumbnails through a temporary, unlisted file association. It also verifies ordinary text files retain shared icon caching.

It does not start the desktop layer or touch the installed application, desktop files, user settings or layout. Test files and a unique temporary HKCU file association are cleaned up in `finally`. The tests exercise layout at simulated DPI values; they do not certify visual text sharpness on a physical display or a particular third-party PSD/AI provider.
