using System.Text.Json;
using Cmux.Core.IPC;

namespace Cmux.Cli;

/// <summary>
/// cmux CLI tool, Windows equivalent of the cmux macOS CLI.
/// Communicates with the running cmux app via named pipes.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "notify" => await HandleNotify(args[1..]),
                "workspace" => await HandleWorkspace(args[1..]),
                "surface" or "tab" => await HandleSurface(args[1..]),
                "split" => await HandleSplit(args[1..]),
                "pane" => await HandlePane(args[1..]),
                "send" or "send-keys" => await HandleSendKeys(args[1..], submitByDefault: false),
                "run" => await HandleSendKeys(args[1..], submitByDefault: true),
                "capture-pane" => await HandleCapturePane(args[1..]),
                "restore" or "restore-session" => await SendAndPrint("RESTORE_SESSION"),
                "status" => await HandleStatus(),
                "help" or "--help" or "-h" => PrintHelp(),
                "version" or "--version" or "-v" => PrintVersion(),
                _ => Error($"Unknown command: {command}"),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Error: Could not connect to cmux. Is it running?");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> HandleNotify(string[] args)
    {
        var parsed = ParseArgs(args);
        var title = parsed.GetValueOrDefault("title", parsed.GetValueOrDefault("_arg0", "Terminal"));
        var body = parsed.GetValueOrDefault("body", parsed.GetValueOrDefault("_arg1", ""));
        var subtitle = parsed.GetValueOrDefault("subtitle");

        var cmdArgs = new Dictionary<string, string>
        {
            ["title"] = title,
            ["body"] = body,
        };
        if (subtitle != null)
            cmdArgs["subtitle"] = subtitle;

        return await SendAndPrint("NOTIFY", cmdArgs);
    }

    private static async Task<int> HandleWorkspace(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux workspace <list|create|select|next|previous>");
            return 1;
        }

        var subcommand = args[0].ToLowerInvariant();
        var parsed = ParseArgs(args[1..]);

        return subcommand switch
        {
            "list" or "ls" => await SendAndPrint("WORKSPACE.LIST"),
            "create" or "new" => await SendAndPrint("WORKSPACE.CREATE", parsed),
            "select" => await SendAndPrint("WORKSPACE.SELECT", parsed),
            "next" => await SendAndPrint("WORKSPACE.NEXT"),
            "previous" or "prev" => await SendAndPrint("WORKSPACE.PREVIOUS"),
            _ => Error($"Unknown workspace command: {subcommand}"),
        };
    }

    private static async Task<int> HandleSurface(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux surface <create|select|next|previous>");
            return 1;
        }

        var subcommand = args[0].ToLowerInvariant();
        var parsed = NormalizeSurfaceArgs(ParseArgs(args[1..]));

        return subcommand switch
        {
            "create" or "new" => await SendAndPrint("SURFACE.CREATE", parsed),
            "select" => await SendAndPrint("SURFACE.SELECT", parsed),
            "next" => await SendAndPrint("SURFACE.NEXT"),
            "previous" or "prev" => await SendAndPrint("SURFACE.PREVIOUS"),
            _ => Error($"Unknown surface command: {subcommand}"),
        };
    }

    private static async Task<int> HandleSplit(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux split <right|down>");
            return 1;
        }

        var direction = args[0].ToLowerInvariant();

        return direction switch
        {
            "right" or "vertical" or "v" => await SendAndPrint("SPLIT.RIGHT"),
            "down" or "horizontal" or "h" => await SendAndPrint("SPLIT.DOWN"),
            _ => Error($"Unknown split direction: {direction}"),
        };
    }

    private static async Task<int> HandlePane(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux pane <list|focus|write|read>");
            return 1;
        }

        var subcommand = args[0].ToLowerInvariant();
        var parsed = NormalizePaneArgs(ParseArgs(args[1..]));

        return subcommand switch
        {
            "list" or "ls" => await SendAndPrint("PANE.LIST", parsed),
            "focus" or "select" => await SendAndPrint("PANE.FOCUS", parsed),
            "write" or "send" => await SendPaneWrite(parsed),
            "read" or "capture" => await SendAndPrint("PANE.READ", parsed),
            _ => Error($"Unknown pane command: {subcommand}"),
        };
    }

    private static async Task<int> HandleSendKeys(string[] args, bool submitByDefault)
    {
        var parsed = NormalizePaneArgs(ParseArgs(args));
        if (submitByDefault)
            parsed["submit"] = "true";

        if (parsed.Remove("enter"))
            parsed["submit"] = "true";

        return await SendPaneWrite(parsed);
    }

    private static async Task<int> HandleCapturePane(string[] args)
    {
        var parsed = NormalizePaneArgs(ParseArgs(args));
        return await SendAndPrint("PANE.READ", parsed);
    }

    private static async Task<int> HandleStatus()
    {
        return await SendAndPrint("STATUS");
    }

    private static async Task<int> SendPaneWrite(Dictionary<string, string> parsed)
    {
        var text = ExtractText(parsed);
        if (text != null)
            parsed["text"] = text;

        var hasSubmit = parsed.TryGetValue("submit", out var submitRaw)
            && bool.TryParse(submitRaw, out var submit)
            && submit;

        if (!hasSubmit && !parsed.ContainsKey("text"))
            return Error("Missing text. Use cmux send-keys \"text\" or cmux run \"command\".");

        return await SendAndPrint("PANE.WRITE", parsed);
    }

    private static async Task<int> SendAndPrint(string command, Dictionary<string, string>? args = null)
    {
        var response = await NamedPipeClient.SendCommand(command, args);

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error))
            {
                Console.Error.WriteLine(error.GetString() ?? "Unknown error");
                return 1;
            }

            var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(pretty);
        }
        catch
        {
            Console.WriteLine(response);
        }

        return 0;
    }

    private static Dictionary<string, string> NormalizeSurfaceArgs(Dictionary<string, string> args)
    {
        CopyAlias(args, "id", "surfaceId");
        CopyAlias(args, "name", "surfaceName");
        CopyAlias(args, "index", "surfaceIndex");
        CopyAlias(args, "workspace", "workspaceName");
        CopyAlias(args, "workspace-id", "workspaceId");
        CopyAlias(args, "workspace-index", "workspaceIndex");
        return args;
    }

    private static Dictionary<string, string> NormalizePaneArgs(Dictionary<string, string> args)
    {
        CopyAlias(args, "workspace", "workspaceName");
        CopyAlias(args, "workspace-id", "workspaceId");
        CopyAlias(args, "workspace-index", "workspaceIndex");
        CopyAlias(args, "surface", "surfaceName");
        CopyAlias(args, "tab", "surfaceName");
        CopyAlias(args, "surface-id", "surfaceId");
        CopyAlias(args, "surface-index", "surfaceIndex");
        CopyAlias(args, "pane", "paneName");
        CopyAlias(args, "pane-id", "paneId");
        CopyAlias(args, "id", "paneId");
        CopyAlias(args, "index", "paneIndex");
        CopyAlias(args, "pane-index", "paneIndex");
        return args;
    }

    private static void CopyAlias(Dictionary<string, string> args, string from, string to)
    {
        if (!args.ContainsKey(to) && args.TryGetValue(from, out var value))
            args[to] = value;
    }

    private static string? ExtractText(Dictionary<string, string> args)
    {
        if (args.TryGetValue("text", out var explicitText))
            return explicitText;

        var positional = args
            .Where(kvp => kvp.Key.StartsWith("_arg", StringComparison.Ordinal))
            .Select(kvp => new
            {
                Index = int.TryParse(kvp.Key[4..], out var index) ? index : int.MaxValue,
                kvp.Value,
            })
            .OrderBy(item => item.Index)
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrEmpty(value))
            .ToList();

        return positional.Count == 0 ? null : string.Join(" ", positional);
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>();
        int positional = 0;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var key = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result[key] = args[i + 1];
                    i++;
                }
                else
                {
                    result[key] = "true";
                }
            }
            else if (arg.StartsWith('-', StringComparison.Ordinal) && arg.Length == 2)
            {
                var key = arg[1..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-', StringComparison.Ordinal))
                {
                    result[key] = args[i + 1];
                    i++;
                }
                else
                {
                    result[key] = "true";
                }
            }
            else
            {
                result[$"_arg{positional}"] = arg;
                positional++;
            }
        }

        return result;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            cmux - Terminal multiplexer for AI coding agents (Windows)

            Usage:
              cmux <command> [options]

            Commands:
              notify                Send a notification
                --title <text>      Notification title (default: "Terminal")
                --body <text>       Notification body
                --subtitle <text>   Notification subtitle

              workspace             Manage workspaces
                list                List all workspaces
                create              Create a new workspace
                  --name <text>     Workspace name
                  --cwd <path>      Workspace working directory
                select              Select a workspace
                  --index <n>       Workspace index (0-based or 1-based)
                  --id <id>         Workspace ID
                  --name <text>     Workspace name
                next                Switch to next workspace
                previous            Switch to previous workspace

              surface, tab          Manage surfaces (tabs within workspace)
                create              Create a new surface
                select              Select a surface
                  --index <n>       Surface index (0-based or 1-based)
                  --id <id>         Surface ID
                  --name <text>     Surface name
                next                Switch to next surface
                previous            Switch to previous surface

              split                 Split the focused pane
                right               Split vertically (left/right)
                down                Split horizontally (top/bottom)

              pane                  Manage panes
                list                List panes in the selected surface
                focus               Focus a pane
                  --index <n>       Pane index (0-based or 1-based)
                  --id <id>         Pane ID
                  --name <text>     Pane name
                write <text>        Write text to a pane
                  --enter           Submit with Enter after writing
                read                Print terminal text from a pane
                  --lines <n>       Number of tail lines to read

              send-keys <text>      Write text to the focused pane
                --enter             Submit with Enter after writing
              run <command>         Write a command and submit it
              capture-pane          Alias for pane read
              restore-session       Reload the saved workspace/session layout
              status                Show cmux status

            Keyboard Shortcuts (in the app):
              Ctrl+N                New workspace
              Ctrl+1-8              Jump to workspace 1-8
              Ctrl+9                Jump to last workspace
              Ctrl+Shift+W          Close workspace
              Ctrl+B                Toggle sidebar
              Ctrl+T                New surface (tab)
              Ctrl+W                Close surface
              Ctrl+D                Split right
              Ctrl+Shift+D          Split down
              Ctrl+Alt+Arrow        Focus pane directionally
              Ctrl+I                Toggle notification panel
              Ctrl+Shift+U          Jump to latest unread
            """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("cmux 1.0.6 (Windows)");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }
}
