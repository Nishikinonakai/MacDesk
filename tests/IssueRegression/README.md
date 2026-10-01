# Windows issue regression checks

Run `dotnet run --project tests/IssueRegression/IssueRegression.csproj -c Release` on Windows with the .NET 10 SDK.

The executable verifies all three embedded Sarasa weights resolve to real resource glyph faces, measures real WPF labels across 6,000 combinations of DPI, font, weight, label size and icon size, and checks rename layout retains the original label width at screen edges. It checks dynamic editor height, scrolling for names taller than the screen, commit/cancel, two distinct thumbnails through a temporary unlisted file association, and ordinary text file shared icon caching.

It does not start the desktop layer or touch the installed application, desktop files, user settings or layout. Test files and a unique temporary HKCU file association are cleaned up in `finally`. The tests exercise layout at simulated DPI values; they do not certify visual text sharpness on a physical display or a particular third-party PSD/AI provider.

GitHub's Windows Server runner cannot produce even ordinary PNG content thumbnails. CI passes `--allow-unavailable-shell-thumbnails`, which permits skipping the content comparison only when the native PNG control also fails. Registered-provider recognition and per-file fallback remain mandatory. A failed custom-extension thumbnail with a working native PNG control is always an error. Desktop runs omit the option and require the full content check.
