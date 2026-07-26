# MacDesk contributor guidance

## Scope

- MacDesk is a native Windows WPF desktop-layer application targeting `net10.0-windows`.
- Treat `layout.json` and `settings.json` as per-user runtime data. They are deliberately ignored and must not be added to Git.
- Do not commit `bin/`, `obj/`, `publish/`, logs, IDE state, installers, or credentials.

## Local development

- Requires the .NET 10 SDK. On macOS/Linux cross-compilation, ensure Windows targeting is enabled.
- Build check:

  ```bash
  dotnet build MacDesk.csproj -c Release -p:EnableWindowsTargeting=true
  ```

- Release artifact:

  ```bash
  dotnet publish -c Release -r win-x64 --self-contained true -o publish
  ```

- The installed application and any test deployment are separate from this Git worktree. Do not overwrite an installed copy as part of normal source sync.

## Dual-machine workflow

- GitHub `origin/main` is the shared source of truth. Before edits run `git fetch origin`, inspect `git status -sb`, and update a clean checkout with `git pull --ff-only`.
- Commit and push a coherent, verified change before continuing the same branch on the other machine. For genuine parallel work, use a named feature branch and merge deliberately.
- Before any remote deployment or release operation, run `git fetch origin && git status` and resolve divergence or local changes first.
- Keep host addresses, access tokens, user-specific install paths, and personal credentials out of the repository and out of prompts/logs.

## Windows validation

- Test desktop-facing behavior only during an approved idle window. Existing user desktop state and the installed release must be preserved.
- Use the app's graceful `--quit` behavior before replacing files in a test deployment; do not force-kill the normal process as a routine deployment step.
